using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MouseTester.Diagnostics
{
    public static class AttributionEngine
    {
        public class Inputs
        {
            public IList<MouseEvent> Events;
            public IReadOnlyList<ColdSample> Cold;
            public IReadOnlyList<EtwAnomaly> Etw;
            public long QpcFrequency;
            public double AnomalyMultiplier = 1.8;
            public double AnomalyMinMs = 0.3;
        }

        private static double baselineDpcPerSec = -1;
        private static double baselineCsPerSec = -1;

        private static string ExtractModule(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return null;
            int idx = detail.IndexOf("mod=", StringComparison.Ordinal);
            if (idx < 0) return null;
            return detail.Substring(idx + 4).Trim();
        }

        private static double ComputeBaselineRate(Inputs input, string eventName)
        {
            if (input.Etw == null || input.Etw.Count < 2 || input.Events.Count < 2) return 0;
            long firstQpc = input.Events[0].pcounter;
            long lastQpc = input.Events[input.Events.Count - 1].pcounter;
            double durationSec = (lastQpc - firstQpc) / (double)input.QpcFrequency;
            if (durationSec <= 0) return 0;
            int count = 0;
            foreach (var a in input.Etw) if (a.EventName == eventName) count++;
            return count / durationSec;
        }

        private static List<ColdSample> ColdSamplesInRange(IReadOnlyList<ColdSample> list, long qpcStart, long qpcEnd)
        {
            var result = new List<ColdSample>();
            if (list == null) return result;
            foreach (var s in list) if (s.QpcTime >= qpcStart && s.QpcTime <= qpcEnd) result.Add(s);
            return result;
        }

        public static List<CauseAttribution> Attribute(Inputs input)
        {
            baselineDpcPerSec = -1;
            baselineCsPerSec = -1;
            var output = new List<CauseAttribution>();
            if (input.Events == null || input.Events.Count < 4) return output;

            double[] intervals = new double[input.Events.Count];
            for (int i = 1; i < input.Events.Count; i++)
                intervals[i] = input.Events[i].ts - input.Events[i - 1].ts;

            double median = MedianOfNonZero(intervals);
            double threshold = Math.Max(input.AnomalyMinMs, median * input.AnomalyMultiplier);

            ColdSample prevCold = null;
            for (int i = 1; i < input.Events.Count; i++)
            {
                double dt = intervals[i];
                if (dt < threshold) continue;

                var ev = input.Events[i];
                var prev = input.Events[i - 1];
                long qpcStart = prev.pcounter;
                long qpcEnd = ev.pcounter;

                var att = new CauseAttribution
                {
                    EventIndex = i,
                    SpikeMs = dt,
                    ExpectedMs = median,
                    QpcStart = qpcStart,
                    QpcEnd = qpcEnd,
                };

                var evidence = new StringBuilder();
                var matches = new List<JitterCause>();

                ColdSample atStart = NearestCold(input.Cold, qpcStart);
                ColdSample atEnd = NearestCold(input.Cold, qpcEnd);

                // Scan ALL cold samples within the spike range (not just start/end)
                // so mid-spike C-states and frequency dips don't get missed.
                long padQpc = (long)(input.QpcFrequency * 0.002); // 2ms pre/post pad
                var samplesInRange = ColdSamplesInRange(input.Cold, qpcStart - padQpc, qpcEnd + padQpc);
                uint maxIdleInRange = 0;
                uint minMhzInRange = uint.MaxValue;
                uint maxMhzObserved = 0;
                bool foregroundChanged = false;
                bool throttlingActivated = false;
                bool minimizedDuringSpike = false;
                bool acChanged = false;
                bool timerResChanged = false;
                int gcGen0Delta = 0, gcGen1Delta = 0, gcGen2Delta = 0;

                if (samplesInRange.Count > 0)
                {
                    var first = samplesInRange[0];
                    var last = samplesInRange[samplesInRange.Count - 1];
                    foregroundChanged = first.ForegroundHwnd != last.ForegroundHwnd;
                    throttlingActivated = !first.ProcessThrottlingEnabled && samplesInRange.Exists(s => s.ProcessThrottlingEnabled);
                    minimizedDuringSpike = !first.Minimized && samplesInRange.Exists(s => s.Minimized);
                    acChanged = first.AcLineStatus != last.AcLineStatus;
                    timerResChanged = first.TimerResolution100ns != last.TimerResolution100ns;
                    gcGen0Delta = (int)(last.Gen0Collections - first.Gen0Collections);
                    gcGen1Delta = (int)(last.Gen1Collections - first.Gen1Collections);
                    gcGen2Delta = (int)(last.Gen2Collections - first.Gen2Collections);

                    foreach (var s in samplesInRange)
                    {
                        if (s.CurrentIdleState > maxIdleInRange) maxIdleInRange = s.CurrentIdleState;
                        if (s.CurrentMhz > 0 && s.CurrentMhz < minMhzInRange) minMhzInRange = s.CurrentMhz;
                        if (s.MaxMhz > maxMhzObserved) maxMhzObserved = s.MaxMhz;
                    }
                }

                if (foregroundChanged)
                {
                    matches.Add(JitterCause.FocusLoss);
                    evidence.Append($"Foreground changed during gap. ");
                }
                if (minimizedDuringSpike)
                {
                    matches.Add(JitterCause.WindowMinimized);
                    evidence.Append("Window minimized during gap. ");
                }
                if (throttlingActivated)
                {
                    matches.Add(JitterCause.ProcessThrottled);
                    evidence.Append("EcoQoS throttling activated. ");
                }
                if (gcGen0Delta + gcGen1Delta + gcGen2Delta > 0)
                {
                    matches.Add(JitterCause.GcPause);
                    evidence.Append($".NET GC fired (Gen0+{gcGen0Delta} Gen1+{gcGen1Delta} Gen2+{gcGen2Delta}). ");
                }
                if (acChanged)
                {
                    matches.Add(JitterCause.AcPowerChange);
                    evidence.Append("Power source changed. ");
                }
                if (maxMhzObserved > 0 && minMhzInRange != uint.MaxValue && minMhzInRange < maxMhzObserved * 0.7)
                {
                    matches.Add(JitterCause.CpuFrequencyDropped);
                    evidence.Append($"Min CPU clock {minMhzInRange} MHz vs max {maxMhzObserved} MHz. ");
                }
                if (maxIdleInRange > 1)
                {
                    matches.Add(JitterCause.CpuParked);
                    evidence.Append($"Cores reached idle state {maxIdleInRange} during gap. ");
                }
                if (timerResChanged)
                {
                    matches.Add(JitterCause.TimerResolutionDropped);
                    evidence.Append("Timer resolution changed during gap. ");
                }

                if (ev.diag != null)
                {
                    if (ev.diag.BurstMember && ev.diag.BurstIndex > 0)
                    {
                        matches.Add(JitterCause.BurstDelivery);
                        evidence.Append($"Burst member #{ev.diag.BurstIndex}. ");
                    }
                    if (prev.diag != null && ev.diag.ProcessorNumber != prev.diag.ProcessorNumber)
                    {
                        matches.Add(JitterCause.ThreadMigrated);
                        evidence.Append($"Thread CPU {prev.diag.ProcessorNumber}→{ev.diag.ProcessorNumber}. ");
                    }
                    if (prev.diag != null && prev.diag.ThreadCycleTime > 0 && ev.diag.ThreadCycleTime > prev.diag.ThreadCycleTime)
                    {
                        ulong cycleDelta = ev.diag.ThreadCycleTime - prev.diag.ThreadCycleTime;
                        double wallNs = (qpcEnd - qpcStart) * 1e9 / Math.Max(1, input.QpcFrequency);
                        // Cycles per nanosecond — if the thread consumed near-zero cycles for the wall
                        // duration, it was scheduled out (preempted) or sleeping.
                        if (cycleDelta < (ulong)(wallNs * 0.05) && wallNs > 1_000_000)
                        {
                            matches.Add(JitterCause.ThreadPreempted);
                            evidence.Append($"Thread used {cycleDelta} cycles in {wallNs / 1e6:0.0}ms (preempted). ");
                        }
                    }
                    // NOTE: per-event WindowHadFocus is NOT used as primary attribution.
                    // RIDEV_INPUTSINK delivers events to MouseTester even when another window has focus
                    // (e.g. testing the mouse over a game). That isn't itself a jitter cause —
                    // we only care about focus *changes during* a spike, which the cold-sample check
                    // above already handles.
                }

                if (input.Etw != null && input.Etw.Count > 0)
                {
                    // Compute baseline density and flag spikes whose density is meaningfully higher.
                    if (baselineDpcPerSec < 0) baselineDpcPerSec = ComputeBaselineRate(input, "DPC");
                    if (baselineCsPerSec < 0) baselineCsPerSec = ComputeBaselineRate(input, "CSwitch");

                    double spikeDurMs = (qpcEnd - qpcStart) * 1000.0 / Math.Max(1, input.QpcFrequency);
                    long padQpcEtw = (long)(input.QpcFrequency * 0.001);
                    int dpcInWindow = 0, isrInWindow = 0, csInWindow = 0;
                    var dpcModules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var isrModules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var a in input.Etw)
                    {
                        if (a.QpcTime < qpcStart - padQpcEtw || a.QpcTime > qpcEnd + padQpcEtw) continue;
                        string mod = ExtractModule(a.Detail);
                        if (a.EventName == "DPC")
                        {
                            dpcInWindow++;
                            if (mod != null) dpcModules[mod] = (dpcModules.TryGetValue(mod, out var c) ? c : 0) + 1;
                        }
                        else if (a.EventName == "ISR")
                        {
                            isrInWindow++;
                            if (mod != null) isrModules[mod] = (isrModules.TryGetValue(mod, out var c) ? c : 0) + 1;
                        }
                        else if (a.EventName == "CSwitch") csInWindow++;
                    }
                    att.TopDpcSourcesInSpike = dpcModules.OrderByDescending(kv => kv.Value).Take(3).ToList();
                    att.TopIsrSourcesInSpike = isrModules.OrderByDescending(kv => kv.Value).Take(3).ToList();

                    string DriverList(IList<KeyValuePair<string, int>> list) =>
                        list.Count == 0 ? "" :
                        " — drivers: " + string.Join(", ", list.Select(kv => kv.Key + " x" + kv.Value));

                    double expectedDpc = baselineDpcPerSec * (spikeDurMs / 1000.0);
                    double expectedCs = baselineCsPerSec * (spikeDurMs / 1000.0);
                    if (expectedDpc > 0 && dpcInWindow > expectedDpc * 2.5 && dpcInWindow >= 3)
                    {
                        matches.Add(JitterCause.DpcStorm);
                        evidence.Append($"DPC density {dpcInWindow}/{expectedDpc:0.0} expected{DriverList(att.TopDpcSourcesInSpike)}. ");
                    }
                    if (expectedCs > 0 && csInWindow > expectedCs * 2.5 && csInWindow >= 3)
                    {
                        matches.Add(JitterCause.ContextSwitchStorm);
                        evidence.Append($"CS density {csInWindow}/{expectedCs:0.0} expected. ");
                    }
                    if (isrInWindow >= 3 && !matches.Contains(JitterCause.DpcStorm))
                    {
                        matches.Add(JitterCause.DpcStorm);
                        evidence.Append($"{isrInWindow} ISRs in spike window{DriverList(att.TopIsrSourcesInSpike)}. ");
                    }
                }

                if (matches.Count == 0)
                {
                    matches.Add(JitterCause.MessagePumpStarved);
                    evidence.Append("No system-level cause detected; UI message pump likely starved. ");
                }

                att.PrimaryCause = matches[0];
                for (int j = 1; j < matches.Count; j++) att.SecondaryCauses.Add(matches[j]);
                att.Evidence = evidence.ToString().Trim();
                att.Confidence = matches.Count > 0 && matches[0] != JitterCause.MessagePumpStarved ? 0.8 : 0.4;
                output.Add(att);

                prevCold = atEnd;
            }

            return output;
        }

        private static double MedianOfNonZero(double[] data)
        {
            var nonZero = data.Where(v => v > 0).OrderBy(v => v).ToArray();
            if (nonZero.Length == 0) return 1.0;
            return nonZero[nonZero.Length / 2];
        }

        private static ColdSample NearestCold(IReadOnlyList<ColdSample> list, long qpc)
        {
            if (list == null || list.Count == 0) return null;
            int lo = 0, hi = list.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].QpcTime < qpc) lo = mid + 1;
                else hi = mid;
            }
            return list[lo];
        }

        private static string Trunc(string s, int max = 40)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
