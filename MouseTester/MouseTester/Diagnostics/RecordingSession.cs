using System;
using System.Diagnostics;
using System.Threading;

namespace MouseTester.Diagnostics
{
    public class RecordingSession : IDisposable
    {
        private bool timerLowered;
        private IntPtr mmcssHandle = IntPtr.Zero;
        private uint mmcssTaskIndex = 0;
        private ProcessPriorityClass originalPriority;
        private ThreadPriority originalThreadPriority;
        private IntPtr originalAffinity;
        private bool priorityChanged;
        private bool affinityChanged;
        private uint timerResolutionBefore100ns;

        public ColdRecorder Cold { get; private set; }
        public EtwSession Etw { get; private set; }
        public bool EtwActive { get; private set; }
        public string EtwError { get; private set; }
        public uint TimerResolutionDuringRecording100ns { get; private set; }

        public void Start(bool wantEtw)
        {
            try
            {
                originalPriority = Process.GetCurrentProcess().PriorityClass;
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                originalThreadPriority = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                priorityChanged = true;
            }
            catch { }

            try
            {
                long mask = (long)Process.GetCurrentProcess().ProcessorAffinity;
                originalAffinity = (IntPtr)mask;
                int cpuCount = Environment.ProcessorCount;
                int targetCpu = cpuCount > 1 ? cpuCount - 1 : 0;
                long newMask = 1L << targetCpu;
                Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)newMask;
                affinityChanged = true;
            }
            catch { }

            try
            {
                NativeMethods.NtQueryTimerResolution(out _, out _, out timerResolutionBefore100ns);
                NativeMethods.TimeBeginPeriod(1);
                timerLowered = true;
            }
            catch { }

            try
            {
                uint idx = 0;
                mmcssHandle = NativeMethods.AvSetMmThreadCharacteristics("Pro Audio", ref idx);
                mmcssTaskIndex = idx;
                if (mmcssHandle != IntPtr.Zero)
                {
                    NativeMethods.AvSetMmThreadPriority(mmcssHandle, 2);
                }
            }
            catch { mmcssHandle = IntPtr.Zero; }

            try
            {
                NativeMethods.NtQueryTimerResolution(out _, out _, out uint cur);
                TimerResolutionDuringRecording100ns = cur;
            }
            catch { }

            Cold = new ColdRecorder(1);
            Cold.Start();

            if (wantEtw)
            {
                Etw = new EtwSession();
                EtwActive = Etw.TryStart();
                if (!EtwActive) EtwError = Etw.LastError;
            }
        }

        public void Stop()
        {
            try { Etw?.Stop(); } catch { }
            try { Cold?.Stop(); } catch { }

            if (mmcssHandle != IntPtr.Zero)
            {
                try { NativeMethods.AvRevertMmThreadCharacteristics(mmcssHandle); } catch { }
                mmcssHandle = IntPtr.Zero;
            }

            if (timerLowered)
            {
                try { NativeMethods.TimeEndPeriod(1); } catch { }
                timerLowered = false;
            }

            if (priorityChanged)
            {
                try { Process.GetCurrentProcess().PriorityClass = originalPriority; } catch { }
                try { Thread.CurrentThread.Priority = originalThreadPriority; } catch { }
                priorityChanged = false;
            }

            if (affinityChanged)
            {
                try { Process.GetCurrentProcess().ProcessorAffinity = originalAffinity; } catch { }
                affinityChanged = false;
            }
        }

        public void Dispose() => Stop();
    }
}
