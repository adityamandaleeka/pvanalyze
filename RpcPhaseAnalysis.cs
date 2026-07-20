using System.Globalization;
using Microsoft.Diagnostics.Tracing;

namespace PVAnalyze;

public enum RpcDirection : byte
{
    Unknown,
    Request,
    Response,
    OneWay
}

public enum RpcCallPhase : byte
{
    Unknown,
    RequestCreated,
    RequestAddressingComplete,
    TransportQueued,
    SerializeStart,
    SerializeStop,
    FlushStart,
    FlushStop,
    FrameDecoded,
    DispatchBuffered,
    DispatchQueued,
    DispatchBatchStart,
    DispatchStart,
    RuntimeReceived,
    ActivationQueued,
    InvocationStart,
    InvocationStop,
    ResponseCreated,
    CallbackStart,
    CompletionSignaled,
    ContinuationStart,
    CallbackComplete,
    Failure,
    Rejection,
    Timeout,
    Cancellation,
    Forwarding,
    Retry
}

public enum RpcSelectionMode : byte
{
    Unknown,
    DeterministicSample,
    ExactTrace
}

public enum RpcResourceKind : byte
{
    None,
    ConnectionSend,
    PipeFlush,
    InboundDispatch,
    Activation,
    Continuation
}

public enum RpcBenchmarkPhase : byte
{
    Unknown,
    Startup,
    WarmupStart,
    WarmupStop,
    MeasurementStart,
    MeasurementStop,
    Shutdown
}

public enum RpcProcessRole : byte
{
    Unknown,
    Driver,
    Target
}

public readonly record struct RpcCallKey(
    ulong TraceIdHigh,
    ulong TraceIdLow,
    int OriginSiloPort,
    int OriginSiloGeneration,
    long CorrelationId)
{
    public string TraceId => TraceIdHigh == 0 && TraceIdLow == 0
        ? ""
        : $"{TraceIdHigh:x16}{TraceIdLow:x16}";
}

public sealed record RpcPhaseEvent(
    double TimestampMs,
    int ProcessId,
    string ProcessName,
    int ThreadId,
    int ProcessorNumber,
    ulong TraceIdHigh,
    ulong TraceIdLow,
    long CorrelationId,
    int OriginSiloPort,
    int OriginSiloGeneration,
    int LocalSiloPort,
    int LocalSiloGeneration,
    RpcDirection Direction,
    RpcCallPhase Phase,
    RpcSelectionMode SelectionMode,
    RpcResourceKind ResourceKind,
    long ResourceId,
    int QueueDepth,
    int RetryCount,
    int ForwardCount,
    int BatchSize,
    int BatchIndex,
    int Detail,
    long DurationTicks,
    long StopwatchFrequency,
    int SampleRate,
    string? RecordedProcessRole = null)
{
    public RpcCallKey Key => new(
        TraceIdHigh,
        TraceIdLow,
        OriginSiloPort,
        OriginSiloGeneration,
        CorrelationId);

    public RpcSiloIdentity OriginSilo => new(OriginSiloPort, OriginSiloGeneration);
    public RpcSiloIdentity LocalSilo => new(LocalSiloPort, LocalSiloGeneration);
    public RpcDirection LogicalDirection => Phase is RpcCallPhase.CompletionSignaled
        or RpcCallPhase.ContinuationStart ? RpcDirection.Response : Direction;
    public string ProcessRole => RecordedProcessRole
        ?? (LocalSiloPort == OriginSiloPort
            && LocalSiloGeneration == OriginSiloGeneration ? "driver" : "target");

    public double? OperationDurationUs => DurationTicks >= 0 && StopwatchFrequency > 0
        ? DurationTicks * 1_000_000d / StopwatchFrequency
        : null;
}

public sealed record RpcPhaseAnalysisOptions(
    int? ProcessId = null,
    string? ProcessName = null,
    string? ProcessRole = null,
    RpcSiloIdentity? OriginSilo = null,
    ulong? TraceIdHigh = null,
    ulong? TraceIdLow = null,
    long? CorrelationId = null,
    double? FromMs = null,
    double? ToMs = null,
    bool SuccessfulOnly = true,
    bool IncludeIncomplete = false,
    double MinCompleteness = 0,
    bool IncludeTimeline = false,
    bool IncludeQueues = false,
    string? QueueFilter = null,
    bool WithCpu = false,
    string ProviderName = RpcPhaseProjector.DefaultProviderName,
    string WindowSource = "explicit");

public static class RpcPhaseProjector
{
    public const string DefaultProviderName = "Microsoft-Orleans-RpcLatency";

    public static bool IsRpcPhaseEvent(TraceEvent traceEvent, string providerName = DefaultProviderName) =>
        traceEvent.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase)
        && traceEvent.EventName.Equals("Phase", StringComparison.OrdinalIgnoreCase);

    public static bool TryProject(
        TraceEvent traceEvent,
        string providerName,
        string processName,
        out RpcPhaseEvent? result)
    {
        result = null;
        if (!IsRpcPhaseEvent(traceEvent, providerName))
        {
            return false;
        }

        try
        {
            var payload = new PayloadReader(traceEvent);
            result = new RpcPhaseEvent(
                traceEvent.TimeStampRelativeMSec,
                traceEvent.ProcessID,
                processName,
                traceEvent.ThreadID,
                traceEvent.ProcessorNumber,
                payload.UInt64("traceIdHigh", 0),
                payload.UInt64("traceIdLow", 1),
                payload.Int64("correlationId", 2),
                payload.Int32("originSiloPort", 3),
                payload.Int32("originSiloGeneration", 4),
                payload.Int32("localSiloPort", 5),
                payload.Int32("localSiloGeneration", 6),
                (RpcDirection)payload.Byte("direction", 7),
                (RpcCallPhase)payload.Byte("phase", 8),
                (RpcSelectionMode)payload.Byte("selectionMode", 9),
                (RpcResourceKind)payload.Byte("resourceKind", 10),
                payload.Int64("resourceId", 11),
                payload.Int32("queueDepth", 12),
                payload.Int32("retryCount", 13),
                payload.Int32("forwardCount", 14),
                payload.Int32("batchSize", 15),
                payload.Int32("batchIndex", 16),
                payload.Int32("detail", 17),
                payload.Int64("durationTicks", 18),
                payload.Int64("stopwatchFrequency", 19),
                payload.Int32("sampleRate", 20));
            if (result.CorrelationId == 0
                || (result.OriginSiloPort == 0 && result.OriginSiloGeneration == 0)
                || result.Direction == RpcDirection.Unknown
                || result.Phase == RpcCallPhase.Unknown
                || !Enum.IsDefined(result.Direction)
                || !Enum.IsDefined(result.Phase)
                || !Enum.IsDefined(result.SelectionMode)
                || !Enum.IsDefined(result.ResourceKind))
            {
                result = null;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is InvalidCastException
            or FormatException
            or OverflowException
            or ArgumentException
            or IndexOutOfRangeException)
        {
            return false;
        }
    }

    public static bool TryParseTraceId(string value, out ulong high, out ulong low)
    {
        var normalized = value.Replace("-", "", StringComparison.Ordinal).Trim();
        if (normalized.Length == 32
            && ulong.TryParse(normalized.AsSpan(0, 16), NumberStyles.HexNumber, null, out high)
            && ulong.TryParse(normalized.AsSpan(16, 16), NumberStyles.HexNumber, null, out low))
        {
            return high != 0 || low != 0;
        }

        high = 0;
        low = 0;
        return false;
    }

    private sealed class PayloadReader
    {
        private readonly TraceEvent _event;
        private readonly Dictionary<string, int> _indices;

        public PayloadReader(TraceEvent traceEvent)
        {
            _event = traceEvent;
            _indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < traceEvent.PayloadNames.Length; index++)
            {
                _indices[traceEvent.PayloadNames[index]] = index;
            }
        }

        public byte Byte(string name, int position) => Convert.ToByte(Value(name, position), CultureInfo.InvariantCulture);
        public int Int32(string name, int position) => Convert.ToInt32(Value(name, position), CultureInfo.InvariantCulture);
        public long Int64(string name, int position) => Convert.ToInt64(Value(name, position), CultureInfo.InvariantCulture);

        public ulong UInt64(string name, int position)
        {
            var value = Value(name, position);
            return value switch
            {
                ulong unsigned => unsigned,
                long signed => unchecked((ulong)signed),
                _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture)
            };
        }

        private object Value(string name, int position)
        {
            var index = _indices.TryGetValue(name, out var namedIndex) ? namedIndex : position;
            return _event.PayloadValue(index)
                ?? throw new InvalidCastException($"Payload '{name}' is null.");
        }
    }
}
