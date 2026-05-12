using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace MouseTester.Diagnostics
{
    public class EtwSession : IDisposable
    {
        private readonly List<EtwAnomaly> anomalies = new List<EtwAnomaly>();
        private TraceEventSession session;
        private Thread thread;
        private long qpcAtStart;
        private DateTime utcAtStart;
        private double qpcFrequency;
        private long totalDpc;
        private long totalIsr;
        private long totalCs;
        private List<KernelModule> kernelModules;
        private readonly Dictionary<string, long> dpcByModule = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> isrByModule = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DriverDpcStats> dpcStatsByModule = new Dictionary<string, DriverDpcStats>(StringComparer.OrdinalIgnoreCase);

        public long TotalDpcCount => System.Threading.Interlocked.Read(ref totalDpc);
        public long TotalIsrCount => System.Threading.Interlocked.Read(ref totalIsr);
        public long TotalCsCount => System.Threading.Interlocked.Read(ref totalCs);

        public IReadOnlyList<KeyValuePair<string, long>> TopDpcSources(int n)
        {
            lock (dpcByModule)
            {
                var copy = new List<KeyValuePair<string, long>>(dpcByModule);
                copy.Sort((a, b) => b.Value.CompareTo(a.Value));
                return copy.GetRange(0, Math.Min(n, copy.Count));
            }
        }

        public IReadOnlyList<KeyValuePair<string, long>> TopIsrSources(int n)
        {
            lock (isrByModule)
            {
                var copy = new List<KeyValuePair<string, long>>(isrByModule);
                copy.Sort((a, b) => b.Value.CompareTo(a.Value));
                return copy.GetRange(0, Math.Min(n, copy.Count));
            }
        }

        public IReadOnlyList<DriverDpcStats> TopDpcByTotalTime(int n)
        {
            lock (dpcStatsByModule)
            {
                var copy = new List<DriverDpcStats>(dpcStatsByModule.Values);
                copy.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
                return copy.GetRange(0, Math.Min(n, copy.Count));
            }
        }

        public IReadOnlyList<DriverDpcStats> TopDpcByMaxLatency(int n)
        {
            lock (dpcStatsByModule)
            {
                var copy = new List<DriverDpcStats>(dpcStatsByModule.Values);
                copy.Sort((a, b) => b.MaxMs.CompareTo(a.MaxMs));
                return copy.GetRange(0, Math.Min(n, copy.Count));
            }
        }

        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }
        public bool RequiresAdmin => !IsAdministrator();

        public IReadOnlyList<EtwAnomaly> Anomalies
        {
            get { lock (anomalies) return anomalies.ToArray(); }
        }

        public bool TryStart()
        {
            if (IsRunning) return true;
            if (!IsAdministrator())
            {
                LastError = "process is not elevated";
                return false;
            }

            try
            {
                NativeMethods.QueryPerformanceFrequency(out long freq);
                qpcFrequency = freq;
                NativeMethods.QueryPerformanceCounter(out qpcAtStart);
                utcAtStart = DateTime.UtcNow;

                // Snapshot the kernel module address-range table so we can attribute each
                // DPC's routine address to a driver. Modules loaded *during* the recording
                // won't be in this map — those DPCs will fall under "(unknown driver)".
                kernelModules = KernelModules.Enumerate();

                session = new TraceEventSession("MouseTester-Diag-" + Guid.NewGuid().ToString("N"));
                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                    KernelTraceEventParser.Keywords.Interrupt |
                    KernelTraceEventParser.Keywords.ContextSwitch);

                // Record ALL DPCs and ISRs (rare-ish events). Sample context switches (very frequent).
                session.Source.Kernel.PerfInfoDPC += data =>
                {
                    System.Threading.Interlocked.Increment(ref totalDpc);
                    string mod = ResolveModule((ulong)data.Routine) ?? "(unknown driver)";
                    double durMs = data.ElapsedTimeMSec;
                    lock (dpcByModule) dpcByModule[mod] = (dpcByModule.TryGetValue(mod, out var c) ? c : 0) + 1;
                    lock (dpcStatsByModule)
                    {
                        if (!dpcStatsByModule.TryGetValue(mod, out var s))
                        {
                            s = new DriverDpcStats { Module = mod };
                            dpcStatsByModule[mod] = s;
                        }
                        s.Count++;
                        s.TotalMs += durMs;
                        if (durMs > s.MaxMs) s.MaxMs = durMs;
                    }
                    RecordAnomaly(data.TimeStamp, "DPC", JitterCause.DpcStorm,
                        "cpu=" + data.ProcessorNumber + " dur=" + durMs.ToString("0.000") + "ms mod=" + mod);
                };
                session.Source.Kernel.PerfInfoISR += data =>
                {
                    System.Threading.Interlocked.Increment(ref totalIsr);
                    string mod = ResolveModule((ulong)data.Routine) ?? "(unknown driver)";
                    lock (isrByModule) isrByModule[mod] = (isrByModule.TryGetValue(mod, out var c) ? c : 0) + 1;
                    RecordAnomaly(data.TimeStamp, "ISR", JitterCause.DpcStorm, "cpu=" + data.ProcessorNumber + " mod=" + mod);
                };
                int csCount = 0;
                session.Source.Kernel.ThreadCSwitch += data =>
                {
                    System.Threading.Interlocked.Increment(ref totalCs);
                    if ((csCount++ & 0xFF) == 0) RecordAnomaly(data.TimeStamp, "CSwitch", JitterCause.ContextSwitchStorm, "cpu=" + data.ProcessorNumber);
                };

                thread = new Thread(() =>
                {
                    try { session.Source.Process(); }
                    catch (Exception ex) { LastError = "Source.Process: " + ex.Message; }
                })
                { IsBackground = true, Name = "MouseTester-ETW" };
                thread.Start();

                IsRunning = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                try { session?.Dispose(); } catch { }
                session = null;
                return false;
            }
        }

        public void Stop()
        {
            try { session?.Dispose(); } catch { }
            session = null;
            thread?.Join(2000);
            thread = null;
            IsRunning = false;
        }

        public void Dispose() => Stop();

        private string ResolveModule(ulong address)
        {
            if (kernelModules == null) return null;
            return KernelModules.Lookup(kernelModules, address);
        }

        private void RecordAnomaly(DateTime timeStamp, string label, JitterCause cause, string detail)
        {
            try
            {
                double secondsSinceStart = (timeStamp.ToUniversalTime() - utcAtStart).TotalSeconds;
                long qpc = qpcAtStart + (long)(secondsSinceStart * qpcFrequency);
                lock (anomalies)
                {
                    anomalies.Add(new EtwAnomaly
                    {
                        QpcTime = qpc,
                        ProviderName = "Kernel",
                        EventName = label,
                        Detail = detail,
                        MapsTo = cause,
                    });
                }
            }
            catch { }
        }

        public IEnumerable<EtwAnomaly> InRange(long qpcStart, long qpcEnd)
        {
            lock (anomalies)
            {
                foreach (var a in anomalies)
                    if (a.QpcTime >= qpcStart && a.QpcTime <= qpcEnd) yield return a;
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(identity)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
