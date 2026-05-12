using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MouseTester.Diagnostics
{
    public class ColdRecorder : IDisposable
    {
        private readonly List<ColdSample> samples = new List<ColdSample>(60_000);
        private Thread thread;
        private volatile bool stop;
        private readonly int intervalMs;
        private readonly int processorCount;

        public ColdRecorder(int intervalMs = 1)
        {
            this.intervalMs = intervalMs;
            this.processorCount = NativeMethods.GetActiveProcessorCount(NativeMethods.ALL_PROCESSOR_GROUPS);
            if (this.processorCount <= 0) this.processorCount = Environment.ProcessorCount;
        }

        public IReadOnlyList<ColdSample> Samples => samples;

        public void Start()
        {
            stop = false;
            thread = new Thread(Run) { IsBackground = true, Name = "MouseTester-ColdRecorder", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            thread?.Join(2000);
            thread = null;
        }

        public void Dispose() => Stop();

        private void Run()
        {
            int procInfoSize = Marshal.SizeOf(typeof(NativeMethods.PROCESSOR_POWER_INFORMATION));
            IntPtr procInfoBuf = Marshal.AllocHGlobal(procInfoSize * processorCount);
            try
            {
                while (!stop)
                {
                    var sample = SampleOnce(procInfoBuf, procInfoSize);
                    lock (samples) samples.Add(sample);
                    Thread.Sleep(intervalMs);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(procInfoBuf);
            }
        }

        private ColdSample SampleOnce(IntPtr procInfoBuf, int procInfoSize)
        {
            NativeMethods.QueryPerformanceCounter(out long qpc);
            var s = new ColdSample { QpcTime = qpc };

            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                s.ForegroundHwnd = fg;
                if (fg != IntPtr.Zero)
                {
                    var sb = new StringBuilder(256);
                    NativeMethods.GetWindowText(fg, sb, sb.Capacity);
                    s.ForegroundTitle = sb.ToString();
                    NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
                    s.ForegroundPid = pid;
                    s.Minimized = NativeMethods.IsIconic(fg);
                }
            }
            catch { }

            try
            {
                NativeMethods.NtQueryTimerResolution(out _, out _, out uint cur);
                s.TimerResolution100ns = cur;
            }
            catch { }

            try
            {
                if (NativeMethods.GetSystemPowerStatus(out var pwr))
                {
                    s.AcLineStatus = pwr.ACLineStatus;
                }
            }
            catch { }

            try
            {
                s.Gen0Collections = (uint)GC.CollectionCount(0);
                s.Gen1Collections = (uint)GC.CollectionCount(1);
                s.Gen2Collections = (uint)GC.CollectionCount(2);
                s.TotalAllocatedBytes = GC.GetTotalMemory(false);
            }
            catch { }

            try
            {
                uint result = NativeMethods.CallNtPowerInformation(NativeMethods.ProcessorInformation, IntPtr.Zero, 0, procInfoBuf, (uint)(procInfoSize * processorCount));
                if (result == 0)
                {
                    uint maxMhz = 0, curMhz = 0, idle = 0;
                    for (int i = 0; i < processorCount; i++)
                    {
                        IntPtr p = IntPtr.Add(procInfoBuf, i * procInfoSize);
                        var info = (NativeMethods.PROCESSOR_POWER_INFORMATION)Marshal.PtrToStructure(p, typeof(NativeMethods.PROCESSOR_POWER_INFORMATION));
                        if (info.MaxMhz > maxMhz) maxMhz = info.MaxMhz;
                        curMhz += info.CurrentMhz;
                        if (info.CurrentIdleState > idle) idle = info.CurrentIdleState;
                    }
                    s.MaxMhz = maxMhz;
                    s.CurrentMhz = (uint)(curMhz / Math.Max(1, processorCount));
                    s.CurrentIdleState = idle;
                }
            }
            catch { }

            try
            {
                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION
                };
                if (NativeMethods.GetProcessInformation(NativeMethods.GetCurrentProcess(), NativeMethods.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf(state)))
                {
                    s.ProcessThrottlingEnabled = (state.StateMask & NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
                }
            }
            catch { }

            return s;
        }

        public ColdSample FindNearest(long qpc)
        {
            lock (samples)
            {
                if (samples.Count == 0) return null;
                int lo = 0, hi = samples.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (samples[mid].QpcTime < qpc) lo = mid + 1;
                    else hi = mid;
                }
                return samples[lo];
            }
        }

        public IEnumerable<ColdSample> InRange(long qpcStart, long qpcEnd)
        {
            lock (samples)
            {
                foreach (var s in samples)
                {
                    if (s.QpcTime >= qpcStart && s.QpcTime <= qpcEnd) yield return s;
                }
            }
        }
    }
}
