using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Gpu.Synchronization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    public sealed class GpuContext : IDisposable
    {
        private const int NsToTicksFractionNumerator = 384;
        private const int NsToTicksFractionDenominator = 625;


        public ManualResetEvent HostInitalized { get; }


        public IRenderer Renderer { get; }


        public GPFifoDevice GPFifo { get; }


        public SynchronizationManager Synchronization { get; }


        public Window Window { get; }


        internal int SequenceNumber { get; private set; }


        internal ulong SyncNumber { get; private set; }


        internal List<ISyncActionHandler> SyncActions { get; }


        internal List<ISyncActionHandler> SyncpointActions { get; }


        internal List<BufferMigration> BufferMigrations { get; }


        internal Queue<Action> DeferredActions { get; }


        internal ConcurrentDictionary<ulong, PhysicalMemory> PhysicalMemoryRegistry { get; }


        internal Capabilities Capabilities;


        public event Action<ShaderCacheState, int, int> ShaderCacheStateChanged;

        private Thread _gpuThread;
        private bool _pendingSync;

        private long _modifiedSequence;


        public GpuContext(IRenderer renderer)
        {
            Renderer = renderer;

            GPFifo = new GPFifoDevice(this);

            Synchronization = new SynchronizationManager();

            Window = new Window(this);

            HostInitalized = new ManualResetEvent(false);

            SyncActions = new List<ISyncActionHandler>();
            SyncpointActions = new List<ISyncActionHandler>();
            BufferMigrations = new List<BufferMigration>();

            DeferredActions = new Queue<Action>();

            PhysicalMemoryRegistry = new ConcurrentDictionary<ulong, PhysicalMemory>();
        }


                public GpuChannel CreateChannel()
        {
            return new GpuChannel(this);
        }


        public MemoryManager CreateMemoryManager(ulong pid)
        {
            if (!PhysicalMemoryRegistry.TryGetValue(pid, out var physicalMemory))
            {
                throw new ArgumentException("The PID is invalid or the process was not registered", nameof(pid));
            }

            return new MemoryManager(physicalMemory);
        }


        public void RegisterProcess(ulong pid, Cpu.IVirtualMemoryManagerTracked cpuMemory)
        {
            var physicalMemory = new PhysicalMemory(this, cpuMemory);
            if (!PhysicalMemoryRegistry.TryAdd(pid, physicalMemory))
            {
                throw new ArgumentException("The PID was already registered", nameof(pid));
            }

            physicalMemory.ShaderCache.ShaderCacheStateChanged += ShaderCacheStateUpdate;
        }

 
        public void UnregisterProcess(ulong pid)
        {
            if (PhysicalMemoryRegistry.TryRemove(pid, out var physicalMemory))
            {
                physicalMemory.ShaderCache.ShaderCacheStateChanged -= ShaderCacheStateUpdate;
                physicalMemory.Dispose();
            }
        }

 
        private static ulong ConvertNanosecondsToTicks(ulong nanoseconds)
        {
 
            ulong divided = nanoseconds / NsToTicksFractionDenominator;

            ulong rounded = divided * NsToTicksFractionDenominator;
            ulong errorBias = (nanoseconds - rounded) * NsToTicksFractionNumerator / NsToTicksFractionDenominator;

            return divided * NsToTicksFractionNumerator + errorBias;
        }

 
        public long GetModifiedSequence()
        {
            return _modifiedSequence++;
        }

 
        public ulong GetTimestamp()
        {
            ulong ticks = ConvertNanosecondsToTicks((ulong)PerformanceCounter.ElapsedNanoseconds);

            if (GraphicsConfig.FastGpuTime)
            {
 
                ticks /= 256;
            }

            return ticks;
        }

 
        private void ShaderCacheStateUpdate(ShaderCacheState state, int current, int total)
        {
            ShaderCacheStateChanged?.Invoke(state, current, total);
        }

 
        public void InitializeShaderCache(CancellationToken cancellationToken)
        {
            HostInitalized.WaitOne();

            foreach (var physicalMemory in PhysicalMemoryRegistry.Values)
            {
                physicalMemory.ShaderCache.Initialize(cancellationToken);
            }
        }

 
        public void SetGpuThread()
        {
            _gpuThread = Thread.CurrentThread;

            Capabilities = Renderer.GetCapabilities();
        }

 
        public bool IsGpuThread()
        {
            return _gpuThread == Thread.CurrentThread;
        }

 
        public void ProcessShaderCacheQueue()
        {
            foreach (var physicalMemory in PhysicalMemoryRegistry.Values)
            {
                physicalMemory.ShaderCache.ProcessShaderCacheQueue();
            }
        }

 
        internal void AdvanceSequence()
        {
            SequenceNumber++;
        }

 
        internal void RegisterBufferMigration(BufferMigration migration)
        {
            BufferMigrations.Add(migration);
            _pendingSync = true;
        }

 
        internal void RegisterSyncAction(ISyncActionHandler action, bool syncpointOnly = false)
        {
            if (syncpointOnly)
            {
                SyncpointActions.Add(action);
            }
            else
            {
                SyncActions.Add(action);
                _pendingSync = true;
            }
        }

 
        internal void CreateHostSyncIfNeeded(HostSyncFlags flags)
        {
            bool syncpoint = flags.HasFlag(HostSyncFlags.Syncpoint);
            bool strict = flags.HasFlag(HostSyncFlags.Strict);
            bool force = flags.HasFlag(HostSyncFlags.Force);

            if (BufferMigrations.Count > 0)
            {
                ulong currentSyncNumber = Renderer.GetCurrentSync();

                for (int i = 0; i < BufferMigrations.Count; i++)
                {
                    BufferMigration migration = BufferMigrations[i];
                    long diff = (long)(currentSyncNumber - migration.SyncNumber);

                    if (diff >= 0)
                    {
                        migration.Dispose();
                        BufferMigrations.RemoveAt(i--);
                    }
                }
            }

            if (force || _pendingSync || (syncpoint && SyncpointActions.Count > 0))
            {
                Renderer.CreateSync(SyncNumber, strict);

                SyncActions.ForEach(action => action.SyncPreAction(syncpoint));
                SyncpointActions.ForEach(action => action.SyncPreAction(syncpoint));

                SyncNumber++;

                SyncActions.RemoveAll(action => action.SyncAction(syncpoint));
                SyncpointActions.RemoveAll(action => action.SyncAction(syncpoint));
            }

            _pendingSync = false;
        }

 
        internal void RunDeferredActions()
        {
            while (DeferredActions.TryDequeue(out Action action))
            {
                action();
            }
        }

 
        public void Dispose()
        {
            Renderer.Dispose();
            GPFifo.Dispose();
            HostInitalized.Dispose();

            // Has to be disposed before processing deferred actions, as it will produce some.
            foreach (var physicalMemory in PhysicalMemoryRegistry.Values)
            {
                physicalMemory.Dispose();
            }

            PhysicalMemoryRegistry.Clear();

            RunDeferredActions();
        }
    }
}
