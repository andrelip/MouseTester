using System;
using System.Collections.Generic;

namespace MouseTester.Diagnostics
{
    public class EventDiagnostics
    {
        public long QpcAtWndProc;
        public long QpcSincePump;
        public uint ProcessorNumber;
        public ulong ThreadCycleTime;
        public bool WindowHadFocus;
        public bool WindowMinimized;
        public bool BurstMember;
        public int BurstIndex;
        public int QueueDepthEstimate;
    }

    public class ColdSample
    {
        public long QpcTime;
        public IntPtr ForegroundHwnd;
        public string ForegroundTitle;
        public uint ForegroundPid;
        public bool Minimized;
        public uint TimerResolution100ns;
        public byte AcLineStatus;
        public uint Gen0Collections;
        public uint Gen1Collections;
        public uint Gen2Collections;
        public long TotalAllocatedBytes;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint CurrentIdleState;
        public bool ProcessThrottlingEnabled;
    }

    public enum JitterCause
    {
        Unknown,
        FocusLoss,
        WindowMinimized,
        ProcessThrottled,
        CpuFrequencyDropped,
        CpuParked,
        ThreadPreempted,
        GcPause,
        MessagePumpStarved,
        BurstDelivery,
        TimerResolutionDropped,
        DpcStorm,
        UsbRetransmit,
        HidReportDrop,
        ContextSwitchStorm,
        AcPowerChange,
        ThreadMigrated,
        OtherProcessHighPriority,
    }

    public class CauseAttribution
    {
        public int EventIndex;
        public double SpikeMs;
        public double ExpectedMs;
        public JitterCause PrimaryCause;
        public List<JitterCause> SecondaryCauses = new List<JitterCause>();
        public string Evidence;
        public double Confidence;
        public long QpcStart;
        public long QpcEnd;
        public List<KeyValuePair<string, int>> TopDpcSourcesInSpike = new List<KeyValuePair<string, int>>();
        public List<KeyValuePair<string, int>> TopIsrSourcesInSpike = new List<KeyValuePair<string, int>>();
    }

    public class EtwAnomaly
    {
        public long QpcTime;
        public string ProviderName;
        public string EventName;
        public double DurationUs;
        public string Detail;
        public JitterCause MapsTo;
    }

    public class TrustReportItem
    {
        public enum Severity { Green, Yellow, Red }
        public string Title;
        public string Detail;
        public Severity Status;
        public string Recommendation;
    }

    public class TrustReport
    {
        public List<TrustReportItem> Items = new List<TrustReportItem>();
        public List<HookProcessInfo> DetectedHooks = new List<HookProcessInfo>();
        public int Score;
        public string Summary;
    }

    public class HookProcessInfo
    {
        public int Pid;
        public string Name;
        public string WindowTitle;
        public string Reason;
    }

    public class DriverDpcStats
    {
        public string Module;
        public long Count;
        public double TotalMs;
        public double MaxMs;
        public double AvgMs => Count > 0 ? TotalMs / Count : 0;
    }
}
