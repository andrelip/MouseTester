using System;
using System.Collections.Generic;
using System.Threading;

namespace MouseTester.Diagnostics
{
    public class SyntheticGenerator
    {
        private Thread thread;
        private volatile bool stop;
        private readonly List<MouseEvent> events = new List<MouseEvent>();
        public int TargetHz { get; private set; }
        public int DurationSeconds { get; private set; }
        public bool IsRunning { get; private set; }
        public Action<List<MouseEvent>> OnComplete;

        public List<MouseEvent> SnapshotEvents()
        {
            lock (events) return new List<MouseEvent>(events);
        }

        public void Start(int hz, int durationSeconds)
        {
            this.TargetHz = hz;
            this.DurationSeconds = durationSeconds;
            stop = false;
            IsRunning = true;
            lock (events) events.Clear();
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "MouseTester-Synthetic-1kHz",
                Priority = ThreadPriority.Highest,
            };
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            thread?.Join(2000);
            thread = null;
            IsRunning = false;
        }

        private void Run()
        {
            try
            {
                int cpuCount = Environment.ProcessorCount;
                // Use the first CPU for the generator (UI/capture pin to last in RecordingSession).
                int targetCpu = 0;
                try
                {
                    NativeMethods.SetThreadAffinityMask(NativeMethods.GetCurrentThread(), (UIntPtr)(1UL << targetCpu));
                }
                catch { }

                NativeMethods.QueryPerformanceFrequency(out long freq);
                long periodTicks = Math.Max(1, freq / TargetHz);
                NativeMethods.QueryPerformanceCounter(out long start);
                long endTime = start + freq * DurationSeconds;
                long nextTick = start + periodTicks;
                int dx = 1;

                while (!stop)
                {
                    NativeMethods.QueryPerformanceCounter(out long now);
                    if (now >= endTime) break;

                    SpinUntil(nextTick);
                    NativeMethods.QueryPerformanceCounter(out now);

                    var ev = new MouseEvent(0, dx, 0, now);
                    var diag = new EventDiagnostics
                    {
                        QpcAtWndProc = now,
                        WindowHadFocus = true,
                    };
                    try { diag.ProcessorNumber = NativeMethods.GetCurrentProcessorNumber(); } catch { }
                    try
                    {
                        ulong cycles;
                        if (NativeMethods.QueryThreadCycleTime(NativeMethods.GetCurrentThread(), out cycles))
                            diag.ThreadCycleTime = cycles;
                    }
                    catch { }
                    ev.diag = diag;

                    lock (events) events.Add(ev);

                    dx = -dx;
                    nextTick += periodTicks;

                    // If we fell more than 5 periods behind (e.g. preempted), skip ahead so we don't burst-fire to catch up.
                    if (now > nextTick + 5 * periodTicks)
                        nextTick = now + periodTicks;
                }
            }
            finally
            {
                IsRunning = false;
                List<MouseEvent> snapshot;
                lock (events) snapshot = new List<MouseEvent>(events);
                OnComplete?.Invoke(snapshot);
            }
        }

        private void SpinUntil(long target)
        {
            long current;
            int spinCount = 0;
            do
            {
                NativeMethods.QueryPerformanceCounter(out current);
                if (current >= target) return;
                if (stop) return;
                // Light yield every ~1000 iterations to avoid hard-locking the core when the gap is large.
                if ((++spinCount & 0x3FF) == 0) Thread.SpinWait(1);
            } while (true);
        }
    }
}
