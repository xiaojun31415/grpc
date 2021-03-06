#region Copyright notice and license

// Copyright 2015 gRPC authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Grpc.Core.Internal;
using Grpc.Core.Logging;
using Grpc.Core.Utils;

namespace Grpc.Core
{
    /// <summary>
    /// Encapsulates initialization and shutdown of gRPC library.
    /// </summary>
    public class GrpcEnvironment
    {
        const int MinDefaultThreadPoolSize = 4;
        const int DefaultBatchContextPoolSharedCapacity = 10000;
        const int DefaultBatchContextPoolThreadLocalCapacity = 64;
        const int DefaultRequestCallContextPoolSharedCapacity = 10000;
        const int DefaultRequestCallContextPoolThreadLocalCapacity = 64;

        static object staticLock = new object();
        static GrpcEnvironment instance;
        static int refCount;
        static int? customThreadPoolSize;
        static int? customCompletionQueueCount;
        static bool inlineHandlers;
        static int batchContextPoolSharedCapacity = DefaultBatchContextPoolSharedCapacity;
        static int batchContextPoolThreadLocalCapacity = DefaultBatchContextPoolThreadLocalCapacity;
        static int requestCallContextPoolSharedCapacity = DefaultRequestCallContextPoolSharedCapacity;
        static int requestCallContextPoolThreadLocalCapacity = DefaultRequestCallContextPoolThreadLocalCapacity;
        static readonly HashSet<Channel> registeredChannels = new HashSet<Channel>();
        static readonly HashSet<Server> registeredServers = new HashSet<Server>();
        static readonly AtomicCounter nativeInitCounter = new AtomicCounter();

        static ILogger logger = new LogLevelFilterLogger(new ConsoleLogger(), LogLevel.Off, true);

        readonly IObjectPool<BatchContextSafeHandle> batchContextPool;
        readonly IObjectPool<RequestCallContextSafeHandle> requestCallContextPool;
        readonly GrpcThreadPool threadPool;
        readonly DebugStats debugStats = new DebugStats();
        readonly AtomicCounter cqPickerCounter = new AtomicCounter();

        bool isShutdown;

        /// <summary>
        /// Returns a reference-counted instance of initialized gRPC environment.
        /// Subsequent invocations return the same instance unless reference count has dropped to zero previously.
        /// </summary>
        internal static GrpcEnvironment AddRef()
        {
            ShutdownHooks.Register();

            lock (staticLock)
            {
                refCount++;
                if (instance == null)
                {
                    instance = new GrpcEnvironment();
                }
                return instance;
            }
        }

        /// <summary>
        /// Decrements the reference count for currently active environment and asynchronously shuts down the gRPC environment if reference count drops to zero.
        /// </summary>
        internal static async Task ReleaseAsync()
        {
            GrpcEnvironment instanceToShutdown = null;
            lock (staticLock)
            {
                GrpcPreconditions.CheckState(refCount > 0);
                refCount--;
                if (refCount == 0)
                {
                    instanceToShutdown = instance;
                    instance = null;
                }
            }

            if (instanceToShutdown != null)
            {
                await instanceToShutdown.ShutdownAsync().ConfigureAwait(false);
            }
        }

        internal static int GetRefCount()
        {
            lock (staticLock)
            {
                return refCount;
            }
        }

        internal static void RegisterChannel(Channel channel)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckNotNull(channel);
                registeredChannels.Add(channel);
            }
        }

        internal static void UnregisterChannel(Channel channel)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckNotNull(channel);
                GrpcPreconditions.CheckArgument(registeredChannels.Remove(channel), "Channel not found in the registered channels set.");
            }
        }

        internal static void RegisterServer(Server server)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckNotNull(server);
                registeredServers.Add(server);
            }
        }

        internal static void UnregisterServer(Server server)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckNotNull(server);
                GrpcPreconditions.CheckArgument(registeredServers.Remove(server), "Server not found in the registered servers set.");
            }
        }

        /// <summary>
        /// Requests shutdown of all channels created by the current process.
        /// </summary>
        public static Task ShutdownChannelsAsync()
        {
            HashSet<Channel> snapshot = null;
            lock (staticLock)
            {
                snapshot = new HashSet<Channel>(registeredChannels);
            }
            return Task.WhenAll(snapshot.Select((channel) => channel.ShutdownAsync()));
        }

        /// <summary>
        /// Requests immediate shutdown of all servers created by the current process.
        /// </summary>
        public static Task KillServersAsync()
        {
            HashSet<Server> snapshot = null;
            lock (staticLock)
            {
                snapshot = new HashSet<Server>(registeredServers);
            }
            return Task.WhenAll(snapshot.Select((server) => server.KillAsync()));
        }

        /// <summary>
        /// Gets application-wide logger used by gRPC.
        /// </summary>
        /// <value>The logger.</value>
        public static ILogger Logger
        {
            get
            {
                return logger;
            }
        }

        /// <summary>
        /// Sets the application-wide logger that should be used by gRPC.
        /// </summary>
        public static void SetLogger(ILogger customLogger)
        {
            GrpcPreconditions.CheckNotNull(customLogger, "customLogger");
            logger = customLogger;
        }

        /// <summary>
        /// Sets the number of threads in the gRPC thread pool that polls for internal RPC events.
        /// Can be only invoked before the <c>GrpcEnviroment</c> is started and cannot be changed afterwards.
        /// Setting thread pool size is an advanced setting and you should only use it if you know what you are doing.
        /// Most users should rely on the default value provided by gRPC library.
        /// Note: this method is part of an experimental API that can change or be removed without any prior notice.
        /// </summary>
        public static void SetThreadPoolSize(int threadCount)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckState(instance == null, "Can only be set before GrpcEnvironment is initialized");
                GrpcPreconditions.CheckArgument(threadCount > 0, "threadCount needs to be a positive number");
                customThreadPoolSize = threadCount;
            }
        }

        /// <summary>
        /// Sets the number of completion queues in the  gRPC thread pool that polls for internal RPC events.
        /// Can be only invoked before the <c>GrpcEnviroment</c> is started and cannot be changed afterwards.
        /// Setting the number of completions queues is an advanced setting and you should only use it if you know what you are doing.
        /// Most users should rely on the default value provided by gRPC library.
        /// Note: this method is part of an experimental API that can change or be removed without any prior notice.
        /// </summary>
        public static void SetCompletionQueueCount(int completionQueueCount)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckState(instance == null, "Can only be set before GrpcEnvironment is initialized");
                GrpcPreconditions.CheckArgument(completionQueueCount > 0, "threadCount needs to be a positive number");
                customCompletionQueueCount = completionQueueCount;
            }
        }

        /// <summary>
        /// By default, gRPC's internal event handlers get offloaded to .NET default thread pool thread (<c>inlineHandlers=false</c>).
        /// Setting <c>inlineHandlers</c> to <c>true</c> will allow scheduling the event handlers directly to
        /// <c>GrpcThreadPool</c> internal threads. That can lead to significant performance gains in some situations,
        /// but requires user to never block in async code (incorrectly written code can easily lead to deadlocks).
        /// Inlining handlers is an advanced setting and you should only use it if you know what you are doing.
        /// Most users should rely on the default value provided by gRPC library.
        /// Note: this method is part of an experimental API that can change or be removed without any prior notice.
        /// Note: <c>inlineHandlers=true</c> was the default in gRPC C# v1.4.x and earlier.
        /// </summary>
        public static void SetHandlerInlining(bool inlineHandlers)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckState(instance == null, "Can only be set before GrpcEnvironment is initialized");
                GrpcEnvironment.inlineHandlers = inlineHandlers;
            }
        }

        /// <summary>
        /// Sets the parameters for a pool that caches batch context instances. Reusing batch context instances
        /// instead of creating a new one for every C core operation helps reducing the GC pressure.
        /// Can be only invoked before the <c>GrpcEnviroment</c> is started and cannot be changed afterwards.
        /// This is an advanced setting and you should only use it if you know what you are doing.
        /// Most users should rely on the default value provided by gRPC library.
        /// Note: this method is part of an experimental API that can change or be removed without any prior notice.
        /// </summary>
        public static void SetBatchContextPoolParams(int sharedCapacity, int threadLocalCapacity)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckState(instance == null, "Can only be set before GrpcEnvironment is initialized");
                GrpcPreconditions.CheckArgument(sharedCapacity >= 0, "Shared capacity needs to be a non-negative number");
                GrpcPreconditions.CheckArgument(threadLocalCapacity >= 0, "Thread local capacity needs to be a non-negative number");
                batchContextPoolSharedCapacity = sharedCapacity;
                batchContextPoolThreadLocalCapacity = threadLocalCapacity;
            }
        }

        /// <summary>
        /// Sets the parameters for a pool that caches request call context instances. Reusing request call context instances
        /// instead of creating a new one for every requested call in C core helps reducing the GC pressure.
        /// Can be only invoked before the <c>GrpcEnviroment</c> is started and cannot be changed afterwards.
        /// This is an advanced setting and you should only use it if you know what you are doing.
        /// Most users should rely on the default value provided by gRPC library.
        /// Note: this method is part of an experimental API that can change or be removed without any prior notice.
        /// </summary>
        public static void SetRequestCallContextPoolParams(int sharedCapacity, int threadLocalCapacity)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckState(instance == null, "Can only be set before GrpcEnvironment is initialized");
                GrpcPreconditions.CheckArgument(sharedCapacity >= 0, "Shared capacity needs to be a non-negative number");
                GrpcPreconditions.CheckArgument(threadLocalCapacity >= 0, "Thread local capacity needs to be a non-negative number");
                requestCallContextPoolSharedCapacity = sharedCapacity;
                requestCallContextPoolThreadLocalCapacity = threadLocalCapacity;
            }
        }

        /// <summary>
        /// Occurs when <c>GrpcEnvironment</c> is about the start the shutdown logic.
        /// If <c>GrpcEnvironment</c> is later initialized and shutdown, the event will be fired again (unless unregistered first).
        /// </summary>
        public static event EventHandler ShuttingDown;

        /// <summary>
        /// Creates gRPC environment.
        /// </summary>
        private GrpcEnvironment()
        {
            GrpcNativeInit();
            batchContextPool = new DefaultObjectPool<BatchContextSafeHandle>(() => BatchContextSafeHandle.Create(), batchContextPoolSharedCapacity, batchContextPoolThreadLocalCapacity);
            requestCallContextPool = new DefaultObjectPool<RequestCallContextSafeHandle>(() => RequestCallContextSafeHandle.Create(), requestCallContextPoolSharedCapacity, requestCallContextPoolThreadLocalCapacity);
            threadPool = new GrpcThreadPool(this, GetThreadPoolSizeOrDefault(), GetCompletionQueueCountOrDefault(), inlineHandlers);
            threadPool.Start();
        }

        /// <summary>
        /// Gets the completion queues used by this gRPC environment.
        /// </summary>
        internal IReadOnlyCollection<CompletionQueueSafeHandle> CompletionQueues
        {
            get
            {
                return this.threadPool.CompletionQueues;
            }
        }

        internal IObjectPool<BatchContextSafeHandle> BatchContextPool => batchContextPool;

        internal IObjectPool<RequestCallContextSafeHandle> RequestCallContextPool => requestCallContextPool;

        internal bool IsAlive
        {
            get
            {
                return this.threadPool.IsAlive;
            }
        }

        /// <summary>
        /// Picks a completion queue in a round-robin fashion.
        /// Shouldn't be invoked on a per-call basis (used at per-channel basis).
        /// </summary>
        internal CompletionQueueSafeHandle PickCompletionQueue()
        {
            var cqIndex = (int) ((cqPickerCounter.Increment() - 1) % this.threadPool.CompletionQueues.Count);
            return this.threadPool.CompletionQueues.ElementAt(cqIndex);
        }

        /// <summary>
        /// Gets the completion queue used by this gRPC environment.
        /// </summary>
        internal DebugStats DebugStats
        {
            get
            {
                return this.debugStats;
            }
        }

        /// <summary>
        /// Gets version of gRPC C core.
        /// </summary>
        internal static string GetCoreVersionString()
        {
            var ptr = NativeMethods.Get().grpcsharp_version_string();  // the pointer is not owned
            return Marshal.PtrToStringAnsi(ptr);
        }

        internal static void GrpcNativeInit()
        {
            if (!IsNativeShutdownAllowed && nativeInitCounter.Count > 0)
            {
                // Normally grpc_init and grpc_shutdown calls should come in pairs (C core does reference counting),
                // but in case we avoid grpc_shutdown calls altogether, calling grpc_init has no effect
                // besides incrementing an internal C core counter that could theoretically overflow.
                // To avoid this theoretical possibility we guard repeated calls to grpc_init()
                // with a 64-bit atomic counter (that can't realistically overflow).
                return;
            }
            NativeMethods.Get().grpcsharp_init();
            nativeInitCounter.Increment();
        }

        internal static void GrpcNativeShutdown()
        {
            if (IsNativeShutdownAllowed)
            {
                NativeMethods.Get().grpcsharp_shutdown();
            }
        }

        /// <summary>
        /// Shuts down this environment.
        /// </summary>
        private async Task ShutdownAsync()
        {
            if (isShutdown)
            {
                throw new InvalidOperationException("ShutdownAsync has already been called");
            }

            await Task.Run(() => ShuttingDown?.Invoke(this, null)).ConfigureAwait(false);

            await threadPool.StopAsync().ConfigureAwait(false);
            requestCallContextPool.Dispose();
            batchContextPool.Dispose();
            GrpcNativeShutdown();
            isShutdown = true;

            debugStats.CheckOK();
        }

        private int GetThreadPoolSizeOrDefault()
        {
            if (customThreadPoolSize.HasValue)
            {
                return customThreadPoolSize.Value;
            }
            // In systems with many cores, use half of the cores for GrpcThreadPool
            // and the other half for .NET thread pool. This heuristic definitely needs
            // more work, but seems to work reasonably well for a start.
            return Math.Max(MinDefaultThreadPoolSize, Environment.ProcessorCount / 2);
        }

        private int GetCompletionQueueCountOrDefault()
        {
            if (customCompletionQueueCount.HasValue)
            {
                return customCompletionQueueCount.Value;
            }
            // by default, create a completion queue for each thread
            return GetThreadPoolSizeOrDefault();
        }

        // On some platforms (specifically iOS), thread local variables in native code
        // require initialization/destruction. By skipping the grpc_shutdown() call,
        // we avoid a potential crash where grpc_shutdown() has already destroyed
        // the thread local variables, but some C core's *_destroy() methods still
        // need to run (e.g. they may be run by finalizer thread which is out of our control)
        // For more context, see https://github.com/grpc/grpc/issues/16294
        private static bool IsNativeShutdownAllowed => !PlatformApis.IsXamarinIOS && !PlatformApis.IsUnityIOS;

        private static class ShutdownHooks
        {
            static object staticLock = new object();
            static bool hooksRegistered;

            public static void Register()
            {
                lock (staticLock)
                {
                    if (!hooksRegistered)
                    {
                        // Under normal circumstances, the user is expected to shutdown all
                        // the gRPC channels and servers before the application exits. The following
                        // hooks provide some extra handling for cases when this is not the case,
                        // in the effort to achieve a reasonable behavior on shutdown.
#if NETSTANDARD1_5
                        // No action required at shutdown on .NET Core
                        // - In-progress P/Invoke calls (such as grpc_completion_queue_next) don't seem
                        //   to prevent a .NET core application from terminating, so no special handling
                        //   is needed.
                        // - .NET core doesn't run finalizers on shutdown, so there's no risk of getting
                        //   a crash because grpc_*_destroy methods for native objects being invoked
                        //   in wrong order.
                        // TODO(jtattermusch): Verify that the shutdown hooks are still not needed
                        // once we add support for new platforms using netstandard (e.g. Xamarin).
#else
                        // On desktop .NET framework and Mono, we need to register for a shutdown
                        // event to explicitly shutdown the GrpcEnvironment.
                        // - On Desktop .NET framework, we need to do a proper shutdown to prevent a crash
                        //   when the framework attempts to run the finalizers for SafeHandle object representing the native
                        //   grpc objects. The finalizers calls the native grpc_*_destroy methods (e.g. grpc_server_destroy)
                        //   in a random order, which is not supported by gRPC.
                        // - On Mono, the process would hang as the GrpcThreadPool threads are sleeping
                        //   in grpc_completion_queue_next P/Invoke invocation and mono won't let the
                        //   process shutdown until the P/Invoke calls return. We achieve that by shutting down
                        //   the completion queue(s) which associated with the GrpcThreadPool, which will
                        //   cause the grpc_completion_queue_next calls to return immediately.
                        AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) => { HandleShutdown(); };
                        AppDomain.CurrentDomain.DomainUnload += (sender, eventArgs) => { HandleShutdown(); };
#endif
                    }
                    hooksRegistered = true;
                }
            }

            /// <summary>
            /// Handler for AppDomain.DomainUnload, AppDomain.ProcessExit and AssemblyLoadContext.Unloading hooks.
            /// </summary>
            private static void HandleShutdown()
            {
                Task.WaitAll(GrpcEnvironment.ShutdownChannelsAsync(), GrpcEnvironment.KillServersAsync());
            }
        }
    }
}
