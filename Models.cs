namespace PVAnalyze;

// Trace info
public record TraceInfo(string Id, string FilePath, double DurationMSec, long EventCount, List<ProcessInfo> Processes);
public record ProcessInfo(int ProcessId, string Name, double CpuMSec);
public record TraceSessionInfo(string Id, string FilePath);

// GC stats
public record GcStatsResponse(List<GcProcessStats> Processes, List<GcEvent>? Timeline);
public record GcProcessStats(int ProcessId, string ProcessName, int TotalGCs, double TotalAllocatedMB,
    double TotalGcCpuMSec, double TotalPauseTimeMSec, double MaxHeapSizeMB, double PauseTimePercent,
    int Gen0Count, int Gen1Count, int Gen2Count, int HeapCount);
public record GcEvent(int ProcessId, string ProcessName, int GcNumber, int Generation, string Type,
    string Reason, double StartTimeMs, double PauseDurationMs, double HeapSizeBeforeMB,
    double HeapSizeAfterMB, double PromotedMB);

// JIT stats
public record JitStatsResponse(List<JitProcessStats> Processes);
public record JitProcessStats(int ProcessId, string ProcessName, long TotalMethodsJitted,
    double TotalJitCpuTimeMSec, long TotalILSize, long TotalNativeSize);

// CPU stacks
public record CpuStacksResponse(int TotalSamples, double TotalCpuTimeMs, string GroupedBy,
    List<CpuStackEntry> Items, double TraceDurationMs, int BucketCount);
public record CpuStackEntry(string Name, double ExclusiveMs, double InclusiveMs, double ExclusivePercent,
    int[]? SampleBuckets = null);

// Events
public record EventsListResponse(List<EventTypeEntry> EventTypes);
public record EventTypeEntry(string Provider, string EventName, int Count);
public record EventsResponse(List<TraceEventEntry> Events);
public record TraceEventEntry(double TimestampMs, string Provider, string EventName, int ProcessId, int ThreadId, string? Message, Dictionary<string, string>? Payload);

// Exceptions
public record ExceptionsResponse(List<ExceptionEntry> Exceptions, Dictionary<string, int> Summary);
public record ExceptionEntry(double TimestampMs, string Type, string Message, int ProcessId, int ThreadId);

// Allocations
public record AllocationsResponse(long TotalAllocations, long TotalBytes, string GroupBy, List<AllocationEntry> Allocations);
public record AllocationEntry(string Name, long Count, long TotalBytes, double AverageBytes, long LargeObjectCount, long LargeObjectBytes);

// Call Tree
public record CallTreeResponse(double TotalMetricMs, int TotalSamples, List<CallTreeNodeDto> Nodes);
public record CallTreeNodeDto(string Name, double InclusiveMs, double ExclusiveMs,
    double InclusivePercent, double ExclusivePercent, int ChildCount, List<CallTreeNodeDto>? Children);
public record CallerCalleeResponse(CallTreeNodeDto Focus, List<CallTreeNodeDto> Callers, List<CallTreeNodeDto> Callees);

// Request models
public record OpenTraceRequest(string FilePath);

// Timeline correlation
public record TimelineResponse(double From, double To, double BucketSizeMs, int BucketCount, Dictionary<string, object[]> Lanes);
public record GcBucket(int GcCount, double TotalPauseMs, double MaxPauseMs, bool HasGen2);
public record CpuBucket(int SampleCount, string? TopMethod);
public record ExceptionBucket(int Count, string? TopType);
public record AllocBucket(long Count, long TotalBytes);
public record JitBucket(int MethodCount, double TotalMs);
public record EventBucket(int Count);

// DATAS (Dynamic Adaptation To Application Sizes)
public record DatasResponse(
    int ProcessId, string ProcessName,
    List<DatasTuningEvent> TuningEvents,
    List<DatasSampleEvent> SampleEvents,
    List<DatasFullGCTuningEvent> FullGCTuningEvents,
    DatasOverview? Overview);

public record DatasOverview(
    int TuningEventCount, int SampleCount, int FullGCTuningCount,
    int MinHeapCount, int MaxHeapCount, int HeapCountChanges,
    double MeanThroughputCostPercent, double MaxThroughputCostPercent,
    double MeanGen0BudgetMB, double MeanSohStableSizeMB);

public class DatasTuningEvent
{
    public DateTime TimeStamp { get; set; }
    public int Version { get; set; }
    public int NewHeapCount { get; set; }
    public int MaxHeapCount { get; set; }
    public int MinHeapCount { get; set; }
    public long GcIndex { get; set; }
    public long TotalSohStableSize { get; set; }
    public float MedianThroughputCostPercent { get; set; }
    public float TcpToConsider { get; set; }
    public float CurrentAccumulation { get; set; }
    public int RecordedTcpCount { get; set; }
    public float RecordedTcpSlope { get; set; }
    public uint NumGcsSinceLastChange { get; set; }
    public int AggFactor { get; set; }
    public int ChangeDecision { get; set; }
    public int AdjReason { get; set; }
    public int HcChangeFreqFactor { get; set; }
    public int HcFreqReason { get; set; }
    public int AdjMetric { get; set; }
}

public class DatasSampleEvent
{
    public DateTime TimeStamp { get; set; }
    public int Version { get; set; }
    public long GcIndex { get; set; }
    public uint ElapsedBetweenGcsUs { get; set; }
    public uint GcPauseTimeUs { get; set; }
    public uint SohMslWaitTimeUs { get; set; }
    public uint UohMslWaitTimeUs { get; set; }
    public long TotalSohStableSize { get; set; }
    public uint Gen0BudgetPerHeap { get; set; }

    /// <summary>Throughput cost: pause / (pause + elapsed), as percentage.</summary>
    public double ThroughputCostPercent =>
        (ElapsedBetweenGcsUs + GcPauseTimeUs) == 0 ? 0 :
        GcPauseTimeUs * 100.0 / (ElapsedBetweenGcsUs + GcPauseTimeUs);
}

public class DatasFullGCTuningEvent
{
    public DateTime TimeStamp { get; set; }
    public int Version { get; set; }
    public int NewHeapCount { get; set; }
    public long GcIndex { get; set; }
    public float MedianGen2Tcp { get; set; }
    public uint NumGen2sSinceLastChange { get; set; }
    public uint Gen2Sample0Age { get; set; }
    public float Gen2Sample0Percent { get; set; }
    public uint Gen2Sample1Age { get; set; }
    public float Gen2Sample1Percent { get; set; }
    public uint Gen2Sample2Age { get; set; }
    public float Gen2Sample2Percent { get; set; }
}

// Point-in-time snapshot
public record SnapshotResponse(double At, double WindowFrom, double WindowTo, SnapshotGc? Gc, SnapshotCpu? Cpu,
    SnapshotExceptions? Exceptions, SnapshotEvents? Events);
public record SnapshotGc(int Count, List<GcEvent> GcEvents);
public record SnapshotCpu(int SampleCount, List<SnapshotCpuMethod> TopMethods);
public record SnapshotCpuMethod(string Name, double ExclusiveMs, double Percent);
public record SnapshotExceptions(int Count, List<ExceptionEntry> Exceptions);
public record SnapshotEvents(int TotalCount, List<SnapshotEventSummary> ByType);
public record SnapshotEventSummary(string Provider, string EventName, int Count);
