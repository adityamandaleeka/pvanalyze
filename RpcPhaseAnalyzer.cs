namespace PVAnalyze;

public static partial class TraceAnalyzer
{
    private static readonly DurationDefinition[] RpcDurations =
    [
        new("placement-addressing", RpcDirection.Request, RpcCallPhase.RequestCreated, RpcDirection.Request, RpcCallPhase.RequestAddressingComplete),
        new("origin-send-routing", RpcDirection.Request, RpcCallPhase.RequestAddressingComplete, RpcDirection.Request, RpcCallPhase.TransportQueued, Transport: true),
        new("request-connection-queue", RpcDirection.Request, RpcCallPhase.TransportQueued, RpcDirection.Request, RpcCallPhase.SerializeStart, Transport: true),
        new("request-serialization", RpcDirection.Request, RpcCallPhase.SerializeStart, RpcDirection.Request, RpcCallPhase.SerializeStop, Transport: true),
        new("request-flush-wait", RpcDirection.Request, RpcCallPhase.SerializeStop, RpcDirection.Request, RpcCallPhase.FlushStop, Transport: true),
        new("request-wire-receive", RpcDirection.Request, RpcCallPhase.FlushStop, RpcDirection.Request, RpcCallPhase.FrameDecoded, Transport: true, SubtractStopOperation: true),
        new("request-inbound-batch-formation", RpcDirection.Request, RpcCallPhase.DispatchBuffered, RpcDirection.Request, RpcCallPhase.DispatchQueued, Transport: true),
        new("request-threadpool-queue", RpcDirection.Request, RpcCallPhase.DispatchQueued, RpcDirection.Request, RpcCallPhase.DispatchBatchStart, Transport: true),
        new("request-batch-head-of-line", RpcDirection.Request, RpcCallPhase.DispatchBatchStart, RpcDirection.Request, RpcCallPhase.DispatchStart, Transport: true),
        new("request-connection-callback", RpcDirection.Request, RpcCallPhase.DispatchStart, RpcDirection.Request, RpcCallPhase.RuntimeReceived, Transport: true),
        new("target-routing-activation-lookup", RpcDirection.Request, RpcCallPhase.RuntimeReceived, RpcDirection.Request, RpcCallPhase.ActivationQueued),
        new("activation-queue", RpcDirection.Request, RpcCallPhase.ActivationQueued, RpcDirection.Request, RpcCallPhase.InvocationStart),
        new("grain-invocation", RpcDirection.Request, RpcCallPhase.InvocationStart, RpcDirection.Request, RpcCallPhase.InvocationStop),
        new("response-construction-routing", RpcDirection.Request, RpcCallPhase.InvocationStop, RpcDirection.Response, RpcCallPhase.TransportQueued, Transport: true),
        new("response-connection-queue", RpcDirection.Response, RpcCallPhase.TransportQueued, RpcDirection.Response, RpcCallPhase.SerializeStart, Transport: true),
        new("response-serialization", RpcDirection.Response, RpcCallPhase.SerializeStart, RpcDirection.Response, RpcCallPhase.SerializeStop, Transport: true),
        new("response-flush-wait", RpcDirection.Response, RpcCallPhase.SerializeStop, RpcDirection.Response, RpcCallPhase.FlushStop, Transport: true),
        new("response-wire-receive", RpcDirection.Response, RpcCallPhase.FlushStop, RpcDirection.Response, RpcCallPhase.FrameDecoded, Transport: true, SubtractStopOperation: true),
        new("response-inbound-batch-formation", RpcDirection.Response, RpcCallPhase.DispatchBuffered, RpcDirection.Response, RpcCallPhase.DispatchQueued, Transport: true),
        new("response-threadpool-queue", RpcDirection.Response, RpcCallPhase.DispatchQueued, RpcDirection.Response, RpcCallPhase.DispatchBatchStart, Transport: true),
        new("response-batch-head-of-line", RpcDirection.Response, RpcCallPhase.DispatchBatchStart, RpcDirection.Response, RpcCallPhase.DispatchStart, Transport: true),
        new("response-connection-callback", RpcDirection.Response, RpcCallPhase.DispatchStart, RpcDirection.Response, RpcCallPhase.RuntimeReceived, Transport: true),
        new("response-runtime-routing", RpcDirection.Response, RpcCallPhase.RuntimeReceived, RpcDirection.Response, RpcCallPhase.CallbackStart),
        new("callback-resolution", RpcDirection.Response, RpcCallPhase.CallbackStart, RpcDirection.Response, RpcCallPhase.CompletionSignaled),
        new("caller-continuation-queue", RpcDirection.Response, RpcCallPhase.CompletionSignaled, RpcDirection.Response, RpcCallPhase.ContinuationStart),
        new("runtime-end-to-end", RpcDirection.Request, RpcCallPhase.RequestCreated, RpcDirection.Response, RpcCallPhase.ContinuationStart)
    ];

    public static RpcLatencyResponse AnalyzeRpcPhases(
        IEnumerable<RpcPhaseEvent> sourceEvents,
        RpcPhaseAnalysisOptions options,
        double sessionEndMs,
        long lostEvents)
    {
        var warnings = new List<string>();
        var calls = new Dictionary<RpcCallKey, RpcAnalyzedCall>();
        var providerEventCount = 0L;

        // This is intentionally one pass. Time filtering happens after state reconstruction.
        foreach (var phaseEvent in sourceEvents)
        {
            providerEventCount++;
            if (!calls.TryGetValue(phaseEvent.Key, out var call))
            {
                call = new RpcAnalyzedCall(phaseEvent.Key);
                calls.Add(phaseEvent.Key, call);
            }

            call.Add(phaseEvent);
        }

        var capturedRates = calls.Values
            .SelectMany(static call => call.Events)
            .Select(static phaseEvent => phaseEvent.SampleRate)
            .Distinct()
            .Order()
            .ToList();

        var fromMs = options.FromMs ?? 0;
        var toMs = options.ToMs ?? sessionEndMs;
        if (!double.IsFinite(fromMs) || !double.IsFinite(toMs) || fromMs < 0 || toMs < fromMs)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The selected time window is invalid.");
        }

        var selected = calls.Values
            .Where(call => CallMatches(call, options))
            .Where(call => call.Events.Any(phaseEvent =>
                phaseEvent.TimestampMs >= fromMs && phaseEvent.TimestampMs <= toMs))
            .OrderBy(static call => call.FirstTimestamp)
            .ToList();
        var participatingRates = selected
            .SelectMany(static call => call.Events)
            .Select(static phaseEvent => phaseEvent.SampleRate)
            .Distinct()
            .Order()
            .ToList();
        if (participatingRates.Count > 1)
        {
            throw new InvalidDataException(
                $"Participating events disagree on sample rate ({string.Join(", ", participatingRates)}).");
        }

        foreach (var call in selected)
        {
            var orderedEvents = call.Events.OrderBy(static phaseEvent => phaseEvent.TimestampMs).ToList();
            call.Events.Clear();
            call.Events.AddRange(orderedEvents);
            AnalyzeCall(call, fromMs, toMs);
        }

        var reportCalls = options.SuccessfulOnly
            ? selected.Where(static call => call.Successful).ToList()
            : selected;
        var excludedFailures = selected.Count - reportCalls.Count;
        if (excludedFailures > 0)
        {
            warnings.Add(
                $"{excludedFailures:N0} unsuccessful call(s) were excluded by --successful-only.");
        }

        var includedCalls = options.IncludeIncomplete
            ? reportCalls.Where(call => call.Completeness >= options.MinCompleteness).ToList()
            : reportCalls.Where(call => call.Complete && call.Completeness >= options.MinCompleteness).ToList();
        var includedCallSet = includedCalls.ToHashSet();

        var phaseEntries = new List<RpcPhaseProfileEntry>();
        foreach (var definition in RpcDurations)
        {
            var eligible = reportCalls
                .Where(call => !definition.Transport || IsTransportApplicable(call, definition))
                .ToList();
            if (eligible.Count == 0)
            {
                continue;
            }
            var values = new List<double>();
            long completeCalls = 0;
            long missingStart = 0;
            long missingStop = 0;
            long duplicate = 0;
            long outOfOrder = 0;
            long crossProcess = 0;

            foreach (var call in eligible)
            {
                var pairs = MatchDuration(call, definition);
                if (pairs.DurationsUs.Count > 0)
                {
                    completeCalls++;
                    if (includedCallSet.Contains(call))
                    {
                        values.AddRange(pairs.DurationsUs);
                    }
                }

                missingStart += pairs.MissingStart;
                missingStop += pairs.MissingStop;
                duplicate += pairs.Duplicate;
                outOfOrder += pairs.OutOfOrder;
                crossProcess += pairs.CrossProcess;
            }

            var completeness = eligible.Count == 0 ? 1 : (double)completeCalls / eligible.Count;
            if (completeness < options.MinCompleteness)
            {
                warnings.Add(
                    $"Phase '{definition.Name}' completeness {completeness:P1} is below {options.MinCompleteness:P1}.");
                continue;
            }

            phaseEntries.Add(new RpcPhaseProfileEntry(
                definition.Name,
                RpcStatistics.Calculate(values),
                eligible.Count,
                completeness,
                new RpcPhaseDiagnostics(
                    missingStart,
                    missingStop,
                    duplicate,
                    outOfOrder,
                    eligible.Count(static call => call.MaxRetryCount > 0),
                    eligible.Count(static call => call.MaxForwardCount > 0),
                    crossProcess),
                null));
        }

        var queueAnalysis = options.IncludeQueues
            ? QueueAnalyzer.Analyze(includedCalls, fromMs, toMs, options.QueueFilter)
            : new QueueAnalysisResult([], new Dictionary<RpcCallKey, double>(), []);
        warnings.AddRange(queueAnalysis.Warnings);

        foreach (var call in selected)
        {
            if (queueAnalysis.CallQueueTimeUs.TryGetValue(call.Key, out var queueTime))
            {
                call.QueueTimeUs = queueTime;
            }
        }

        var incomplete = reportCalls.Count(static call => !call.Complete);
        if (incomplete > 0)
        {
            warnings.Add($"{incomplete:N0} selected call(s) are incomplete or window-truncated.");
        }

        var duplicateCalls = reportCalls.Count(static call => call.DuplicateCount > 0);
        if (duplicateCalls > 0)
        {
            warnings.Add($"{duplicateCalls:N0} call(s) contain duplicate phase occurrences.");
        }

        var outOfOrderCalls = reportCalls.Count(static call => call.OutOfOrderCount > 0);
        if (outOfOrderCalls > 0)
        {
            warnings.Add($"{outOfOrderCalls:N0} call(s) contain out-of-order phase occurrences.");
        }

        var localIdentityPids = selected
            .SelectMany(static call => call.Events)
            .GroupBy(static phaseEvent => phaseEvent.LocalSilo)
            .Where(static group => group.Select(static phaseEvent => phaseEvent.ProcessId).Distinct().Skip(1).Any())
            .Select(static group => group.Key)
            .ToList();
        foreach (var identity in localIdentityPids)
        {
            warnings.Add($"Silo identity {identity} is emitted by more than one process.");
        }

        if (lostEvents > 0)
        {
            warnings.Add($"The trace reports {lostEvents:N0} lost event(s); phase completeness may be biased.");
        }

        if (providerEventCount == 0)
        {
            warnings.Add($"No {options.ProviderName}/Phase events were found.");
        }
        else if (selected.Count == 0)
        {
            warnings.Add("No correlated calls matched the selected filters and time window.");
        }

        if (options.TraceIdHigh.HasValue && selected.Count > 1)
        {
            throw new InvalidDataException(
                $"The exact trace selector matched {selected.Count:N0} correlated calls; "
                + "use an origin silo or correlation ID to select one root call.");
        }
        if (options.CorrelationId.HasValue
            && selected.Select(static call => (call.Key.OriginSiloPort, call.Key.OriginSiloGeneration))
                .Distinct().Skip(1).Any())
        {
            warnings.Add(
                "The correlation ID matched multiple origin silo identities; "
                + "use --origin-silo to disambiguate it.");
        }

        if (options.WithCpu)
        {
            warnings.Add(
                "Sampled CPU attribution is unavailable for fixed-schema point-event intervals; "
                + "reported phase values remain wall-clock durations.");
        }

        List<RpcTimelineEntry>? timeline = null;
        if (options.IncludeTimeline)
        {
            if (reportCalls.Count == 1)
            {
                timeline = CreateTimeline(reportCalls[0]);
            }
            else
            {
                warnings.Add(
                    $"Timeline requires exactly one selected call; the filters selected {reportCalls.Count:N0}.");
            }
        }

        var processIdentities = selected
            .SelectMany(static call => call.Events)
            .GroupBy(static phaseEvent => (phaseEvent.ProcessId, phaseEvent.ProcessName))
            .Select(group => new RpcProcessIdentity(
                group.Key.ProcessId,
                group.Key.ProcessName,
                group.Select(static phaseEvent => phaseEvent.ProcessRole).Distinct().Order().ToList(),
                group.Select(static phaseEvent => phaseEvent.LocalSilo).Distinct().OrderBy(static silo => silo.Port).ThenBy(static silo => silo.Generation).ToList()))
            .OrderBy(static process => process.ProcessId)
            .ToList();

        var callDtos = reportCalls.Select(call => new RpcCallSummary(
            call.Key.TraceId,
            call.Key.CorrelationId,
            new RpcSiloIdentity(call.Key.OriginSiloPort, call.Key.OriginSiloGeneration),
            call.Complete,
            call.Successful,
            call.WindowTruncated,
            call.Completeness,
            call.MaxRetryCount,
            call.MaxForwardCount,
            call.EndToEndUs,
            call.QueueTimeUs,
            call.Warnings.ToList())).ToList();

        int? sampleRate = participatingRates.Count == 1 ? participatingRates[0] : null;
        var deterministicCalls = reportCalls.Count(static call => call.Events.Any(
            static phaseEvent => phaseEvent.SelectionMode == RpcSelectionMode.DeterministicSample));
        var exactCalls = reportCalls.Count - deterministicCalls;
        var estimatedSourceCalls = exactCalls
            + deterministicCalls * (long)(sampleRate is > 0 ? sampleRate.Value : 1);
        var completenessTotal = reportCalls.Count == 0
            ? 0
            : reportCalls.Average(static call => call.Completeness);
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sampleRate.HasValue)
        {
            arguments["SampleRate"] = sampleRate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new RpcLatencyResponse(
            new RpcTraceWindow(fromMs, toMs, options.WindowSource),
            processIdentities,
            new RpcProviderMetadata(
                options.ProviderName,
                providerEventCount,
                capturedRates,
                arguments,
                lostEvents,
                exactCalls,
                deterministicCalls),
            reportCalls.Count,
            estimatedSourceCalls,
            sampleRate,
            completenessTotal,
            RpcStatistics.PercentileMethod,
            phaseEntries,
            queueAnalysis.Queues,
            RpcStatistics.Calculate(includedCalls.Select(static call => call.QueueTimeUs)),
            callDtos,
            timeline,
            warnings.Distinct().ToList());
    }

    private static bool CallMatches(RpcAnalyzedCall call, RpcPhaseAnalysisOptions options)
    {
        if (options.OriginSilo is { } origin
            && (call.Key.OriginSiloPort != origin.Port
                || call.Key.OriginSiloGeneration != origin.Generation))
        {
            return false;
        }

        if (options.TraceIdHigh.HasValue
            && (call.Key.TraceIdHigh != options.TraceIdHigh.Value
                || call.Key.TraceIdLow != options.TraceIdLow.GetValueOrDefault()))
        {
            return false;
        }

        if (options.CorrelationId.HasValue && call.Key.CorrelationId != options.CorrelationId.Value)
        {
            return false;
        }

        if (options.ProcessId.HasValue
            && !call.Events.Any(phaseEvent => phaseEvent.ProcessId == options.ProcessId.Value))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.ProcessName)
            && !call.Events.Any(phaseEvent => phaseEvent.ProcessName.Contains(
                options.ProcessName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.ProcessRole))
        {
            return true;
        }

        var originEvents = call.Events.Where(phaseEvent =>
            phaseEvent.LocalSiloPort == call.Key.OriginSiloPort
            && phaseEvent.LocalSiloGeneration == call.Key.OriginSiloGeneration).ToList();
        return (originEvents.Count > 0 ? originEvents : call.Events)
            .Any(phaseEvent => RoleMatches(phaseEvent.ProcessRole, options.ProcessRole));
    }

    private static bool RoleMatches(string actual, string requested)
    {
        var normalized = requested.Trim().ToLowerInvariant();
        return normalized switch
        {
            "origin" or "source" or "client" => actual == "driver",
            "callee" or "server" => actual == "target",
            _ => actual.Equals(normalized, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static void AnalyzeCall(RpcAnalyzedCall call, double fromMs, double toMs)
    {
        call.Successful = !call.Events.Any(static phaseEvent =>
            phaseEvent.Phase is RpcCallPhase.Failure
                or RpcCallPhase.Rejection
                or RpcCallPhase.Timeout
                or RpcCallPhase.Cancellation);
        call.MaxRetryCount = call.Events.Max(static phaseEvent => phaseEvent.RetryCount);
        call.MaxForwardCount = call.Events.Max(static phaseEvent => phaseEvent.ForwardCount);
        call.WindowTruncated = call.FirstTimestamp < fromMs || call.LastTimestamp > toMs;

        call.DuplicateCount = call.Buckets.Values.Sum(static bucket => Math.Max(0, bucket.Count - 1));

        foreach (var attempt in call.Events
            .Where(static phaseEvent => PhaseRank(phaseEvent) >= 0)
            .GroupBy(static phaseEvent => (phaseEvent.RetryCount, phaseEvent.ForwardCount)))
        {
            var previousRank = -1;
            foreach (var phaseEvent in attempt)
            {
                var rank = PhaseRank(phaseEvent);
                if (rank < previousRank)
                {
                    call.OutOfOrderCount++;
                }
                previousRank = Math.Max(previousRank, rank);
            }
        }

        var expected = ExpectedEvents(call);
        var present = expected.Count(item => call.Events.Any(phaseEvent =>
            phaseEvent.LogicalDirection == item.Direction && phaseEvent.Phase == item.Phase));
        call.Completeness = expected.Count == 0 ? 0 : (double)present / expected.Count;

        var endToEnd = MatchDuration(call, RpcDurations[^1]);
        call.EndToEndUs = endToEnd.DurationsUs.Count > 0 ? endToEnd.DurationsUs[0] : null;
        call.Complete = call.Completeness >= 1
            && call.EndToEndUs.HasValue
            && !call.WindowTruncated
            && call.OutOfOrderCount == 0
            && call.DuplicateCount == 0;

        if (call.WindowTruncated)
        {
            call.Warnings.Add("Selected window truncates this call.");
        }
        if (call.DuplicateCount > 0)
        {
            call.Warnings.Add($"{call.DuplicateCount} duplicate phase occurrence(s).");
        }
        if (call.OutOfOrderCount > 0)
        {
            call.Warnings.Add($"{call.OutOfOrderCount} phase-order violation(s).");
        }
    }

    private static List<(RpcDirection Direction, RpcCallPhase Phase)> ExpectedEvents(RpcAnalyzedCall call)
    {
        var result = new List<(RpcDirection, RpcCallPhase)>
        {
            (RpcDirection.Request, RpcCallPhase.RequestCreated),
            (RpcDirection.Request, RpcCallPhase.RequestAddressingComplete),
            (RpcDirection.Request, RpcCallPhase.RuntimeReceived),
            (RpcDirection.Request, RpcCallPhase.ActivationQueued),
            (RpcDirection.Request, RpcCallPhase.InvocationStart),
            (RpcDirection.Request, RpcCallPhase.InvocationStop),
            (RpcDirection.Response, RpcCallPhase.ResponseCreated),
            (RpcDirection.Response, RpcCallPhase.CallbackStart),
            (RpcDirection.Response, RpcCallPhase.CompletionSignaled),
            (RpcDirection.Response, RpcCallPhase.ContinuationStart)
        };

        AddTransportExpected(call, result, RpcDirection.Request);
        AddTransportExpected(call, result, RpcDirection.Response);
        return result;
    }

    private static void AddTransportExpected(
        RpcAnalyzedCall call,
        List<(RpcDirection, RpcCallPhase)> result,
        RpcDirection direction)
    {
        var transportPhases = new[]
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
        if (call.Events.Any(phaseEvent =>
            phaseEvent.LogicalDirection == direction && transportPhases.Contains(phaseEvent.Phase)))
        {
            result.AddRange(transportPhases.Select(phase => (direction, phase)));
            if (direction == RpcDirection.Response)
            {
                result.Add((direction, RpcCallPhase.RuntimeReceived));
            }
        }
    }

    private static bool IsTransportApplicable(RpcAnalyzedCall call, DurationDefinition definition)
    {
        var direction = definition.StartPhase == RpcCallPhase.InvocationStop
            ? definition.StopDirection
            : definition.StartDirection;
        return call.Events.Any(phaseEvent =>
            phaseEvent.LogicalDirection == direction
            && phaseEvent.Phase is RpcCallPhase.TransportQueued
                or RpcCallPhase.SerializeStart
                or RpcCallPhase.SerializeStop
                or RpcCallPhase.FlushStart
                or RpcCallPhase.FlushStop
                or RpcCallPhase.FrameDecoded);
    }

    private static DurationMatch MatchDuration(RpcAnalyzedCall call, DurationDefinition definition)
    {
        var starts = call.Events
            .Where(phaseEvent => phaseEvent.LogicalDirection == definition.StartDirection
                && phaseEvent.Phase == definition.StartPhase)
            .ToList();
        var stops = call.Events
            .Where(phaseEvent => phaseEvent.LogicalDirection == definition.StopDirection
                && phaseEvent.Phase == definition.StopPhase)
            .ToList();
        var durations = new List<double>();
        var usedStarts = new HashSet<int>();
        long outOfOrder = 0;
        long crossProcess = 0;

        foreach (var stop in stops)
        {
            var startIndex = -1;
            for (var index = starts.Count - 1; index >= 0; index--)
            {
                if (usedStarts.Contains(index)
                    || starts[index].RetryCount != stop.RetryCount
                    || starts[index].ForwardCount != stop.ForwardCount
                    || starts[index].TimestampMs > stop.TimestampMs)
                {
                    continue;
                }

                startIndex = index;
                break;
            }

            if (startIndex < 0)
            {
                if (starts.Any(start => start.RetryCount == stop.RetryCount
                    && start.ForwardCount == stop.ForwardCount))
                {
                    outOfOrder++;
                }
                continue;
            }

            usedStarts.Add(startIndex);
            var start = starts[startIndex];
            var durationUs = (stop.TimestampMs - start.TimestampMs) * 1000;
            if (definition.SubtractStopOperation)
            {
                durationUs -= stop.OperationDurationUs ?? 0;
            }

            if (durationUs < 0)
            {
                outOfOrder++;
                continue;
            }

            if (start.ProcessId != stop.ProcessId)
            {
                crossProcess++;
            }
            durations.Add(durationUs);
        }

        return new DurationMatch(
            durations,
            Math.Max(0, stops.Count - durations.Count),
            Math.Max(0, starts.Count - durations.Count),
            Math.Max(0, starts.Count - starts.Select(static item => (
                item.RetryCount,
                item.ForwardCount,
                item.LocalSilo)).Distinct().Count())
                + Math.Max(0, stops.Count - stops.Select(static item => (
                    item.RetryCount,
                    item.ForwardCount,
                    item.LocalSilo)).Distinct().Count()),
            outOfOrder,
            crossProcess);
    }

    private static int PhaseRank(RpcPhaseEvent phaseEvent)
    {
        var offset = phaseEvent.LogicalDirection == RpcDirection.Response ? 20 : 0;
        return phaseEvent.Phase switch
        {
            RpcCallPhase.RequestCreated => 0,
            RpcCallPhase.RequestAddressingComplete => 1,
            RpcCallPhase.TransportQueued => offset + 2,
            RpcCallPhase.SerializeStart => offset + 3,
            RpcCallPhase.SerializeStop => offset + 4,
            RpcCallPhase.FlushStart => offset + 5,
            RpcCallPhase.FlushStop => offset + 6,
            RpcCallPhase.FrameDecoded => offset + 7,
            RpcCallPhase.DispatchBuffered => offset + 8,
            RpcCallPhase.DispatchQueued => offset + 9,
            RpcCallPhase.DispatchBatchStart => offset + 10,
            RpcCallPhase.DispatchStart => offset + 11,
            RpcCallPhase.RuntimeReceived => offset + 12,
            RpcCallPhase.ActivationQueued => 13,
            RpcCallPhase.InvocationStart => 14,
            RpcCallPhase.InvocationStop => 15,
            RpcCallPhase.ResponseCreated => 20,
            RpcCallPhase.CallbackStart => 33,
            RpcCallPhase.CompletionSignaled => 34,
            RpcCallPhase.ContinuationStart => 35,
            _ => -1
        };
    }

    private static List<RpcTimelineEntry> CreateTimeline(RpcAnalyzedCall call)
    {
        var start = call.FirstTimestamp;
        var previous = start;
        var result = new List<RpcTimelineEntry>(call.Events.Count);
        foreach (var phaseEvent in call.Events)
        {
            result.Add(new RpcTimelineEntry(
                phaseEvent.TimestampMs,
                (phaseEvent.TimestampMs - start) * 1000,
                (phaseEvent.TimestampMs - previous) * 1000,
                phaseEvent.ProcessId,
                phaseEvent.ProcessName,
                phaseEvent.ProcessRole,
                phaseEvent.ThreadId,
                phaseEvent.ProcessorNumber,
                phaseEvent.LocalSilo,
                phaseEvent.LogicalDirection.ToString(),
                phaseEvent.Phase.ToString(),
                phaseEvent.ResourceKind.ToString(),
                phaseEvent.ResourceId,
                phaseEvent.QueueDepth >= 0 ? phaseEvent.QueueDepth : null,
                phaseEvent.RetryCount,
                phaseEvent.ForwardCount,
                phaseEvent.BatchSize >= 0 ? phaseEvent.BatchSize : null,
                phaseEvent.BatchIndex >= 0 ? phaseEvent.BatchIndex : null,
                phaseEvent.OperationDurationUs));
            previous = phaseEvent.TimestampMs;
        }
        return result;
    }

    private sealed record DurationDefinition(
        string Name,
        RpcDirection StartDirection,
        RpcCallPhase StartPhase,
        RpcDirection StopDirection,
        RpcCallPhase StopPhase,
        bool Transport = false,
        bool SubtractStopOperation = false);

    private sealed record DurationMatch(
        List<double> DurationsUs,
        long MissingStart,
        long MissingStop,
        long Duplicate,
        long OutOfOrder,
        long CrossProcess);
}

internal sealed class RpcAnalyzedCall(RpcCallKey key)
{
    public RpcCallKey Key { get; } = key;
    public List<RpcPhaseEvent> Events { get; } = [];
    public Dictionary<RpcOccurrenceKey, List<RpcPhaseEvent>> Buckets { get; } = [];
    public List<string> Warnings { get; } = [];
    public double FirstTimestamp => Events.Count == 0 ? 0 : Events.Min(static item => item.TimestampMs);
    public double LastTimestamp => Events.Count == 0 ? 0 : Events.Max(static item => item.TimestampMs);
    public bool Complete { get; set; }
    public bool Successful { get; set; }
    public bool WindowTruncated { get; set; }
    public double Completeness { get; set; }
    public int MaxRetryCount { get; set; }
    public int MaxForwardCount { get; set; }
    public int DuplicateCount { get; set; }
    public int OutOfOrderCount { get; set; }
    public double? EndToEndUs { get; set; }
    public double QueueTimeUs { get; set; }

    public void Add(RpcPhaseEvent phaseEvent)
    {
        Events.Add(phaseEvent);
        var occurrenceKey = new RpcOccurrenceKey(
            phaseEvent.LogicalDirection,
            phaseEvent.Phase,
            phaseEvent.RetryCount,
            phaseEvent.ForwardCount,
            phaseEvent.LocalSilo);
        if (!Buckets.TryGetValue(occurrenceKey, out var bucket))
        {
            bucket = [];
            Buckets.Add(occurrenceKey, bucket);
        }
        bucket.Add(phaseEvent);
    }
}

internal readonly record struct RpcOccurrenceKey(
    RpcDirection Direction,
    RpcCallPhase Phase,
    int RetryCount,
    int ForwardCount,
    RpcSiloIdentity LocalSilo);

internal static class RpcStatistics
{
    public const string PercentileMethod = "nearest-rank (ceil(p*n), minimum rank 1)";

    public static DistributionStats Calculate(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0)
        {
            return new DistributionStats(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var mean = values.Average();
        var variance = values.Sum(value => (value - mean) * (value - mean)) / values.Length;
        var median = Percentile(values, 0.5);
        var deviations = values.Select(value => Math.Abs(value - median)).Order().ToArray();
        return new DistributionStats(
            values.Length,
            mean,
            Percentile(values, 0.5),
            Percentile(values, 0.9),
            Percentile(values, 0.99),
            Percentile(values, 0.999),
            values[^1],
            Math.Sqrt(variance),
            Percentile(deviations, 0.5));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * sorted.Length));
        return sorted[Math.Min(sorted.Length - 1, rank - 1)];
    }

    public static double Correlation(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count || left.Count < 2)
        {
            return 0;
        }

        var leftMean = left.Average();
        var rightMean = right.Average();
        double covariance = 0;
        double leftVariance = 0;
        double rightVariance = 0;
        for (var index = 0; index < left.Count; index++)
        {
            var leftDelta = left[index] - leftMean;
            var rightDelta = right[index] - rightMean;
            covariance += leftDelta * rightDelta;
            leftVariance += leftDelta * leftDelta;
            rightVariance += rightDelta * rightDelta;
        }

        var denominator = Math.Sqrt(leftVariance * rightVariance);
        return denominator == 0 ? 0 : covariance / denominator;
    }
}
