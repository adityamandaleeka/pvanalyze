using System.Text.Json;
using PVAnalyze.Commands;
using Xunit;

namespace PVAnalyze.Tests;

public class RpcPhaseAnalyzerTests
{
    [Fact]
    public void FinalizedOrleansSchemaValuesAreStable()
    {
        Assert.Equal(
            Enumerable.Range(1, 27),
            new[]
            {
                RpcCallPhase.RequestCreated,
                RpcCallPhase.RequestAddressingComplete,
                RpcCallPhase.TransportQueued,
                RpcCallPhase.SerializeStart,
                RpcCallPhase.SerializeStop,
                RpcCallPhase.FlushStart,
                RpcCallPhase.FlushStop,
                RpcCallPhase.FrameDecoded,
                RpcCallPhase.DispatchBuffered,
                RpcCallPhase.DispatchQueued,
                RpcCallPhase.DispatchBatchStart,
                RpcCallPhase.DispatchStart,
                RpcCallPhase.RuntimeReceived,
                RpcCallPhase.ActivationQueued,
                RpcCallPhase.InvocationStart,
                RpcCallPhase.InvocationStop,
                RpcCallPhase.ResponseCreated,
                RpcCallPhase.CallbackStart,
                RpcCallPhase.CompletionSignaled,
                RpcCallPhase.ContinuationStart,
                RpcCallPhase.CallbackComplete,
                RpcCallPhase.Failure,
                RpcCallPhase.Rejection,
                RpcCallPhase.Timeout,
                RpcCallPhase.Cancellation,
                RpcCallPhase.Forwarding,
                RpcCallPhase.Retry
            }.Select(static value => (int)value));
        Assert.Equal([1, 2, 3], Enum.GetValues<RpcDirection>().Skip(1).Select(static value => (int)value));
        Assert.Equal([1, 2], Enum.GetValues<RpcSelectionMode>().Skip(1).Select(static value => (int)value));
        Assert.Equal([0, 1, 2, 3, 4, 5], Enum.GetValues<RpcResourceKind>().Select(static value => (int)value));
        Assert.Equal([1, 2, 3, 4, 5, 6], Enum.GetValues<RpcBenchmarkPhase>().Skip(1).Select(static value => (int)value));
        Assert.Equal([1, 2], Enum.GetValues<RpcProcessRole>().Skip(1).Select(static value => (int)value));
    }

    [Fact]
    public void ReconstructsTwoProcessCallAndQueues()
    {
        var events = CreateCompleteCall();
        var result = TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(
                IncludeTimeline: true,
                IncludeQueues: true,
                IncludeIncomplete: true),
            100,
            0);

        var call = Assert.Single(result.Calls);
        Assert.True(call.Complete);
        Assert.Equal(35_000, call.EndToEndUs);
        Assert.Equal(2, result.Processes.Count);
        Assert.Equal(26, result.Phases.Count);
        Assert.All(result.Phases, phase => Assert.Equal(1, phase.Duration.Count));
        Assert.Contains(result.Phases, phase =>
            phase.Name == "request-wire-receive"
            && phase.Duration.Count == 1
            && phase.Duration.MeanUs == 1_999);
        Assert.Contains(result.Phases, phase =>
            phase.Name == "response-runtime-routing"
            && phase.Diagnostics.CrossProcess == 0);
        Assert.Contains(result.Queues, queue =>
            queue.Name == "request-connection"
            && queue.CompletedWaits == 1
            && queue.Wait.MeanUs == 1_000
            && queue.MeanDepth == 3);
        Assert.Equal(events.Count, result.Timeline!.Count);
        Assert.Equal("driver", result.Timeline[0].ProcessRole);
        Assert.Equal("target", result.Timeline.First(item => item.ProcessId == 202).ProcessRole);
    }

    [Fact]
    public void ReportsDuplicatesAndWindowTruncation()
    {
        var events = CreateCompleteCall();
        events.Add(events.First(item => item.Phase == RpcCallPhase.SerializeStart));
        var result = TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(
                FromMs: 10,
                ToMs: 40,
                IncludeIncomplete: true,
                WindowSource: "explicit"),
            100,
            0);

        var call = Assert.Single(result.Calls);
        Assert.False(call.Complete);
        Assert.True(call.WindowTruncated);
        Assert.Contains(call.Warnings, warning => warning.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TreatsLocalDeliveryAsCompleteWithoutTransportPhases()
    {
        var transport = new HashSet<RpcCallPhase>
        {
            RpcCallPhase.TransportQueued,
            RpcCallPhase.SerializeStart,
            RpcCallPhase.SerializeStop,
            RpcCallPhase.FlushStart,
            RpcCallPhase.FlushStop,
            RpcCallPhase.FrameDecoded,
            RpcCallPhase.DispatchBuffered,
            RpcCallPhase.DispatchQueued,
            RpcCallPhase.DispatchBatchStart,
            RpcCallPhase.DispatchStart
        };
        var events = CreateCompleteCall()
            .Where(phaseEvent => !transport.Contains(phaseEvent.Phase))
            .Select(phaseEvent => phaseEvent with
            {
                ProcessId = 101,
                ProcessName = "driver",
                LocalSiloPort = phaseEvent.OriginSiloPort,
                RecordedProcessRole = "driver"
            })
            .ToList();
        var result = TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(IncludeIncomplete: true),
            100,
            0);

        Assert.True(Assert.Single(result.Calls).Complete);
        Assert.DoesNotContain(result.Phases, phase => phase.Name == "request-connection-queue");
    }

    [Fact]
    public void DiagnosesOutOfOrderAndRetryAttempts()
    {
        var events = CreateCompleteCall();
        var serializeStart = events.FindIndex(item =>
            item.Direction == RpcDirection.Request && item.Phase == RpcCallPhase.SerializeStart);
        events[serializeStart] = events[serializeStart] with { TimestampMs = 4.5 };
        events.Add(events.First(item =>
            item.Direction == RpcDirection.Response && item.Phase == RpcCallPhase.TransportQueued) with
        {
            TimestampMs = 21.1,
            RetryCount = 1
        });
        events.Add(events.First(item =>
            item.Direction == RpcDirection.Response && item.Phase == RpcCallPhase.SerializeStart) with
        {
            TimestampMs = 22.1,
            RetryCount = 1
        });

        var result = TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(IncludeIncomplete: true),
            100,
            0);

        Assert.Contains(result.Warnings, warning => warning.Contains("out-of-order", StringComparison.OrdinalIgnoreCase));
        var responseQueue = Assert.Single(
            result.Phases,
            phase => phase.Name == "response-connection-queue");
        Assert.Equal(2, responseQueue.Duration.Count);
        Assert.Equal(1, responseQueue.Diagnostics.Retry);
    }

    [Fact]
    public void DiagnosesMissingStartsAndStops()
    {
        var events = CreateCompleteCall();
        events.RemoveAll(item =>
            item.Direction == RpcDirection.Request && item.Phase == RpcCallPhase.SerializeStart);

        var result = TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(IncludeIncomplete: true),
            100,
            0);

        Assert.Equal(1, Assert.Single(
            result.Phases,
            phase => phase.Name == "request-connection-queue").Diagnostics.MissingStop);
        Assert.Equal(1, Assert.Single(
            result.Phases,
            phase => phase.Name == "request-serialization").Diagnostics.MissingStart);
    }

    [Fact]
    public void AggregatesSharedBatchFlushTimestamps()
    {
        var first = CreateCompleteCall();
        var second = CreateCompleteCall(correlationId: 8, offset: 0.1);
        second = second.Select(item =>
            item.Direction == RpcDirection.Request
                && item.Phase is RpcCallPhase.FlushStart or RpcCallPhase.FlushStop
                ? item with
                {
                    TimestampMs = first.Single(firstItem =>
                        firstItem.Direction == item.Direction
                        && firstItem.Phase == item.Phase).TimestampMs
                }
                : item).ToList();
        first.AddRange(second);

        var result = TraceAnalyzer.AnalyzeRpcPhases(
            first,
            new RpcPhaseAnalysisOptions(IncludeIncomplete: true, IncludeQueues: true),
            100,
            0);

        var backpressure = Assert.Single(
            result.Queues,
            queue => queue.Name == "request-backpressure");
        Assert.Equal(2, backpressure.CompletedWaits);
        Assert.Equal(800, backpressure.Wait.MeanUs, 6);
    }

    [Fact]
    public void CorrelatesSampledIdentityAcrossProcesses()
    {
        var events = CreateCompleteCall()
            .Select(phaseEvent => phaseEvent with
            {
                TraceIdHigh = 0,
                TraceIdLow = 0,
                SelectionMode = RpcSelectionMode.DeterministicSample
            })
            .ToList();
        var result = TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(ProcessId: 202, IncludeIncomplete: true),
            100,
            0);

        var call = Assert.Single(result.Calls);
        Assert.Equal("", call.TraceId);
        Assert.Equal(64, result.EstimatedSourceCallCount);
    }

    [Fact]
    public void AppliesOriginRoleTraceCorrelationAndQueueFilters()
    {
        var events = CreateCompleteCall();
        events.AddRange(CreateCompleteCall(correlationId: 8, originPort: 3333, offset: 50));
        var result = TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(
                ProcessRole: "driver",
                OriginSilo: new RpcSiloIdentity(1111, 1),
                TraceIdHigh: 1,
                TraceIdLow: 2,
                CorrelationId: 7,
                IncludeIncomplete: true,
                IncludeQueues: true,
                QueueFilter: "activation"),
            200,
            0);

        Assert.Single(result.Calls);
        var queue = Assert.Single(result.Queues);
        Assert.Equal("request-activation", queue.Name);
    }

    [Fact]
    public void RejectsAmbiguousExactTraceSelection()
    {
        var events = CreateCompleteCall();
        events.AddRange(CreateCompleteCall(correlationId: 8, offset: 50));

        Assert.Throws<InvalidDataException>(() => TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(
                TraceIdHigh: 1,
                TraceIdLow: 2,
                IncludeIncomplete: true),
            200,
            0));
    }

    [Fact]
    public void UsesRecordedBenchmarkProcessRoleInsteadOfSiloHeuristic()
    {
        var events = CreateCompleteCall()
            .Select(phaseEvent => phaseEvent with
            {
                RecordedProcessRole = phaseEvent.ProcessId == 101 ? "target" : "driver"
            })
            .ToList();

        var result = TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(ProcessRole: "target", IncludeIncomplete: true),
            100,
            0);

        Assert.Single(result.Calls);
        Assert.Contains(result.Processes, process =>
            process.ProcessId == 101 && process.Roles.SequenceEqual(["target"]));
    }

    [Fact]
    public void RejectsMismatchedSampleRates()
    {
        var events = CreateCompleteCall();
        events[^1] = events[^1] with { SampleRate = 128 };
        Assert.Throws<InvalidDataException>(() => TraceAnalyzer.AnalyzeRpcPhases(
            events,
            new RpcPhaseAnalysisOptions(IncludeIncomplete: true),
            100,
            0));
    }

    [Fact]
    public void JsonSchemaContainsProviderCompletenessAndDeviation()
    {
        var result = TraceAnalyzer.AnalyzeRpcPhases(
            CreateCompleteCall(),
            new RpcPhaseAnalysisOptions(IncludeIncomplete: true, IncludeQueues: true),
            100,
            0);
        var json = JsonSerializer.Serialize(result, LatencyJsonContext.Default.RpcLatencyResponse);

        Assert.Contains("\"provider\"", json);
        Assert.Contains("\"completeness\"", json);
        Assert.Contains("\"standardDeviationUs\"", json);
        Assert.Contains("\"percentileMethod\"", json);
    }

    [Fact]
    public void UsesNearestRankPercentilesAndPopulationDeviation()
    {
        var stats = RpcStatistics.Calculate([1, 2, 100]);

        Assert.Equal(2, stats.P50Us);
        Assert.Equal(100, stats.P90Us);
        Assert.Equal(1, stats.MedianAbsoluteDeviationUs);
        Assert.Equal(46.435, stats.StandardDeviationUs, 3);
    }

    private static List<RpcPhaseEvent> CreateCompleteCall(
        long correlationId = 7,
        int originPort = 1111,
        double offset = 0)
    {
        var result = new List<RpcPhaseEvent>();
        Add(RpcDirection.Request, RpcCallPhase.RequestCreated, 0, 101, originPort, 1);
        Add(RpcDirection.Request, RpcCallPhase.RequestAddressingComplete, 1, 101, originPort, 1);
        Add(RpcDirection.Request, RpcCallPhase.TransportQueued, 2, 101, originPort, 1, RpcResourceKind.ConnectionSend, 10, 3);
        Add(RpcDirection.Request, RpcCallPhase.SerializeStart, 3, 101, originPort, 1, RpcResourceKind.ConnectionSend, 10);
        Add(RpcDirection.Request, RpcCallPhase.SerializeStop, 4, 101, originPort, 1);
        Add(RpcDirection.Request, RpcCallPhase.FlushStart, 4.2, 101, originPort, 1, RpcResourceKind.PipeFlush, 10);
        Add(RpcDirection.Request, RpcCallPhase.FlushStop, 5, 101, originPort, 1, RpcResourceKind.PipeFlush, 10);
        Add(RpcDirection.Request, RpcCallPhase.FrameDecoded, 7, 202, 2222, 1, durationTicks: 1);
        Add(RpcDirection.Request, RpcCallPhase.DispatchBuffered, 8, 202, 2222, 1, RpcResourceKind.InboundDispatch, 20, 1, 4, 2);
        Add(RpcDirection.Request, RpcCallPhase.DispatchQueued, 9, 202, 2222, 1, RpcResourceKind.InboundDispatch, 20, 4, 4, 2);
        Add(RpcDirection.Request, RpcCallPhase.DispatchBatchStart, 10, 202, 2222, 1, RpcResourceKind.InboundDispatch, 20, 7, 4, 2);
        Add(RpcDirection.Request, RpcCallPhase.DispatchStart, 11, 202, 2222, 1, RpcResourceKind.InboundDispatch, 20, batchSize: 4, batchIndex: 2);
        Add(RpcDirection.Request, RpcCallPhase.RuntimeReceived, 12, 202, 2222, 1);
        Add(RpcDirection.Request, RpcCallPhase.ActivationQueued, 13, 202, 2222, 1, RpcResourceKind.Activation, 30, 2);
        Add(RpcDirection.Request, RpcCallPhase.InvocationStart, 14, 202, 2222, 1, RpcResourceKind.Activation, 30);
        Add(RpcDirection.Request, RpcCallPhase.InvocationStop, 20, 202, 2222, 1);
        Add(RpcDirection.Response, RpcCallPhase.ResponseCreated, 20.5, 202, 2222, 1);
        Add(RpcDirection.Response, RpcCallPhase.TransportQueued, 21, 202, 2222, 1, RpcResourceKind.ConnectionSend, 40, 2);
        Add(RpcDirection.Response, RpcCallPhase.SerializeStart, 22, 202, 2222, 1, RpcResourceKind.ConnectionSend, 40);
        Add(RpcDirection.Response, RpcCallPhase.SerializeStop, 23, 202, 2222, 1);
        Add(RpcDirection.Response, RpcCallPhase.FlushStart, 23.2, 202, 2222, 1, RpcResourceKind.PipeFlush, 40);
        Add(RpcDirection.Response, RpcCallPhase.FlushStop, 24, 202, 2222, 1, RpcResourceKind.PipeFlush, 40);
        Add(RpcDirection.Response, RpcCallPhase.FrameDecoded, 26, 101, originPort, 1, durationTicks: 1);
        Add(RpcDirection.Response, RpcCallPhase.DispatchBuffered, 27, 101, originPort, 1, RpcResourceKind.InboundDispatch, 50, 1, 3, 1);
        Add(RpcDirection.Response, RpcCallPhase.DispatchQueued, 28, 101, originPort, 1, RpcResourceKind.InboundDispatch, 50, 3, 3, 1);
        Add(RpcDirection.Response, RpcCallPhase.DispatchBatchStart, 29, 101, originPort, 1, RpcResourceKind.InboundDispatch, 50, 5, 3, 1);
        Add(RpcDirection.Response, RpcCallPhase.DispatchStart, 30, 101, originPort, 1, RpcResourceKind.InboundDispatch, 50, batchSize: 3, batchIndex: 1);
        Add(RpcDirection.Response, RpcCallPhase.RuntimeReceived, 31, 101, originPort, 1);
        Add(RpcDirection.Response, RpcCallPhase.CallbackStart, 32, 101, originPort, 1);
        // Completion-source events retain the request trace context; the analyzer
        // projects their unique phase semantics onto the logical response path.
        Add(RpcDirection.Request, RpcCallPhase.CompletionSignaled, 33, 101, originPort, 1, RpcResourceKind.Continuation, 60, 8);
        Add(RpcDirection.Request, RpcCallPhase.ContinuationStart, 35, 101, originPort, 1, RpcResourceKind.Continuation, 60);
        Add(RpcDirection.Response, RpcCallPhase.CallbackComplete, 36, 101, originPort, 1);
        return result;

        void Add(
            RpcDirection direction,
            RpcCallPhase phase,
            double time,
            int pid,
            int localPort,
            int localGeneration,
            RpcResourceKind resource = RpcResourceKind.None,
            long resourceId = 0,
            int depth = -1,
            int batchSize = 0,
            int batchIndex = -1,
            long durationTicks = 0)
        {
            result.Add(new RpcPhaseEvent(
                offset + time,
                pid,
                pid == 101 ? "driver" : "target",
                pid == 101 ? 11 : 22,
                pid == 101 ? 1 : 2,
                1,
                2,
                correlationId,
                originPort,
                1,
                localPort,
                localGeneration,
                direction,
                phase,
                RpcSelectionMode.ExactTrace,
                resource,
                resourceId,
                depth,
                0,
                0,
                batchSize,
                batchIndex,
                0,
                durationTicks,
                1_000_000,
                64,
                pid == 101 ? "driver" : "target"));
        }
    }
}
