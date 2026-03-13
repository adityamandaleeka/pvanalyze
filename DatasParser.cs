using System;

namespace PVAnalyze;

/// <summary>
/// Parses DATAS (Dynamic Adaptation To Application Sizes) dynamic events
/// from the raw byte[] payloads stored in TraceGC.DynamicEvents.
///
/// The binary format is little-endian packed fields, serialized by the runtime's
/// gc_event::Serialize (gcevent_serializers.h). Every payload starts with a
/// uint16 version field.
/// </summary>
public static class DatasParser
{
    public const string TuningEventName = "SizeAdaptationTuning";
    public const string SampleEventName = "SizeAdaptationSample";
    public const string FullGCTuningEventName = "SizeAdaptationFullGCTuning";

    /// <summary>
    /// Parse a SizeAdaptationTuning event payload (heap count decisions).
    /// Layout (56 bytes):
    ///   0: uint16 version
    ///   2: uint16 new_heap_count
    ///   4: uint16 max_heap_count
    ///   6: uint16 min_heap_count
    ///   8: uint64 gc_index
    ///  16: uint64 total_soh_stable_size
    ///  24: float  median_throughput_cost_percent
    ///  28: float  tcp_to_consider
    ///  32: float  current_around_target_accumulation
    ///  36: uint16 recorded_tcp_count
    ///  38: float  recorded_tcp_slope
    ///  42: uint32 num_gcs_since_last_change
    ///  46: uint8  agg_factor
    ///  47: uint16 change_decision
    ///  49: uint16 adj_reason
    ///  51: uint16 hc_change_freq_factor
    ///  53: uint16 hc_freq_reason
    ///  55: uint8  adj_metric
    /// </summary>
    public static DatasTuningEvent? ParseTuning(byte[] data, DateTime timeStamp)
    {
        if (data.Length < 56)
            return null;

        return new DatasTuningEvent
        {
            TimeStamp = timeStamp,
            Version = BitConverter.ToUInt16(data, 0),
            NewHeapCount = BitConverter.ToUInt16(data, 2),
            MaxHeapCount = BitConverter.ToUInt16(data, 4),
            MinHeapCount = BitConverter.ToUInt16(data, 6),
            GcIndex = (long)BitConverter.ToUInt64(data, 8),
            TotalSohStableSize = (long)BitConverter.ToUInt64(data, 16),
            MedianThroughputCostPercent = BitConverter.ToSingle(data, 24),
            TcpToConsider = BitConverter.ToSingle(data, 28),
            CurrentAccumulation = BitConverter.ToSingle(data, 32),
            RecordedTcpCount = BitConverter.ToUInt16(data, 36),
            RecordedTcpSlope = BitConverter.ToSingle(data, 38),
            NumGcsSinceLastChange = BitConverter.ToUInt32(data, 42),
            AggFactor = data[46],
            ChangeDecision = BitConverter.ToUInt16(data, 47),
            AdjReason = BitConverter.ToUInt16(data, 49),
            HcChangeFreqFactor = BitConverter.ToUInt16(data, 51),
            HcFreqReason = BitConverter.ToUInt16(data, 53),
            AdjMetric = data[55],
        };
    }

    /// <summary>
    /// Parse a SizeAdaptationSample event payload (per-GC sample data).
    /// Layout (38 bytes):
    ///   0: uint16 version
    ///   2: uint64 gc_index
    ///  10: uint32 elapsed_between_gcs (microseconds)
    ///  14: uint32 gc_pause_time (microseconds)
    ///  18: uint32 soh_msl_wait_time (microseconds)
    ///  22: uint32 uoh_msl_wait_time (microseconds)
    ///  26: uint64 total_soh_stable_size
    ///  34: uint32 gen0_budget_per_heap
    /// </summary>
    public static DatasSampleEvent? ParseSample(byte[] data, DateTime timeStamp)
    {
        if (data.Length < 38)
            return null;

        return new DatasSampleEvent
        {
            TimeStamp = timeStamp,
            Version = BitConverter.ToUInt16(data, 0),
            GcIndex = (long)BitConverter.ToUInt64(data, 2),
            ElapsedBetweenGcsUs = BitConverter.ToUInt32(data, 10),
            GcPauseTimeUs = BitConverter.ToUInt32(data, 14),
            SohMslWaitTimeUs = BitConverter.ToUInt32(data, 18),
            UohMslWaitTimeUs = BitConverter.ToUInt32(data, 22),
            TotalSohStableSize = (long)BitConverter.ToUInt64(data, 26),
            Gen0BudgetPerHeap = BitConverter.ToUInt32(data, 34),
        };
    }

    /// <summary>
    /// Parse a SizeAdaptationFullGCTuning event payload (gen2 backstop decisions).
    /// Layout (44 bytes):
    ///   0: uint16 version
    ///   2: uint16 new_heap_count
    ///   4: uint64 gc_index
    ///  12: float  median_gen2_tcp
    ///  16: uint32 num_gen2s_since_last_change
    ///  20: uint32 gen2_sample0_age (gc_index delta)
    ///  24: float  gen2_sample0_percent
    ///  28: uint32 gen2_sample1_age
    ///  32: float  gen2_sample1_percent
    ///  36: uint32 gen2_sample2_age
    ///  40: float  gen2_sample2_percent
    /// </summary>
    public static DatasFullGCTuningEvent? ParseFullGCTuning(byte[] data, DateTime timeStamp)
    {
        if (data.Length < 44)
            return null;

        return new DatasFullGCTuningEvent
        {
            TimeStamp = timeStamp,
            Version = BitConverter.ToUInt16(data, 0),
            NewHeapCount = BitConverter.ToUInt16(data, 2),
            GcIndex = (long)BitConverter.ToUInt64(data, 4),
            MedianGen2Tcp = BitConverter.ToSingle(data, 12),
            NumGen2sSinceLastChange = BitConverter.ToUInt32(data, 16),
            Gen2Sample0Age = BitConverter.ToUInt32(data, 20),
            Gen2Sample0Percent = BitConverter.ToSingle(data, 24),
            Gen2Sample1Age = BitConverter.ToUInt32(data, 28),
            Gen2Sample1Percent = BitConverter.ToSingle(data, 32),
            Gen2Sample2Age = BitConverter.ToUInt32(data, 36),
            Gen2Sample2Percent = BitConverter.ToSingle(data, 40),
        };
    }
}
