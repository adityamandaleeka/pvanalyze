namespace PVAnalyze;

internal sealed record QueueAnalysisResult(
    List<QueueProfileEntry> Queues,
    Dictionary<RpcCallKey, double> CallQueueTimeUs,
    List<string> Warnings);

internal static class QueueAnalyzer
{
    private static readonly QueueDefinition[] Definitions =
    [
        new("request-connection", "connection-send", RpcDirection.Request, RpcCallPhase.TransportQueued, RpcCallPhase.SerializeStart, "origin-send-routing", "request-serialization"),
        new("request-backpressure", "pipe-backpressure", RpcDirection.Request, RpcCallPhase.FlushStart, RpcCallPhase.FlushStop, "request-serialization", "request-wire-receive"),
        new("request-inbound-batch", "inbound-dispatch", RpcDirection.Request, RpcCallPhase.DispatchBuffered, RpcCallPhase.DispatchQueued, "request-wire-receive", "request-threadpool"),
        new("request-threadpool", "threadpool", RpcDirection.Request, RpcCallPhase.DispatchQueued, RpcCallPhase.DispatchBatchStart, "request-inbound-batch", "request-head-of-line", Contextual: true),
        new("request-head-of-line", "batch-head-of-line", RpcDirection.Request, RpcCallPhase.DispatchBatchStart, RpcCallPhase.DispatchStart, "request-threadpool", "request-connection-callback"),
        new("request-activation", "activation", RpcDirection.Request, RpcCallPhase.ActivationQueued, RpcCallPhase.InvocationStart, "target-routing-activation-lookup", "grain-invocation"),
        new("response-connection", "connection-send", RpcDirection.Response, RpcCallPhase.TransportQueued, RpcCallPhase.SerializeStart, "response-construction-routing", "response-serialization"),
        new("response-backpressure", "pipe-backpressure", RpcDirection.Response, RpcCallPhase.FlushStart, RpcCallPhase.FlushStop, "response-serialization", "response-wire-receive"),
        new("response-inbound-batch", "inbound-dispatch", RpcDirection.Response, RpcCallPhase.DispatchBuffered, RpcCallPhase.DispatchQueued, "response-wire-receive", "response-threadpool"),
        new("response-threadpool", "threadpool", RpcDirection.Response, RpcCallPhase.DispatchQueued, RpcCallPhase.DispatchBatchStart, "response-inbound-batch", "response-head-of-line", Contextual: true),
        new("response-head-of-line", "batch-head-of-line", RpcDirection.Response, RpcCallPhase.DispatchBatchStart, RpcCallPhase.DispatchStart, "response-threadpool", "response-connection-callback"),
        new("caller-continuation", "continuation", RpcDirection.Response, RpcCallPhase.CompletionSignaled, RpcCallPhase.ContinuationStart, "callback-resolution", null, Contextual: true)
    ];

    public static QueueAnalysisResult Analyze(
        IReadOnlyList<RpcAnalyzedCall> calls,
        double fromMs,
        double toMs,
        string? queueFilter)
    {
        var results = new List<QueueProfileEntry>();
        var callTotals = new Dictionary<RpcCallKey, double>();
        var callsByKey = calls.ToDictionary(static call => call.Key);
        var warnings = new List<string>();
        var durationSeconds = Math.Max(0, toMs - fromMs) / 1000;

        foreach (var definition in Definitions.Where(definition => MatchesFilter(definition, queueFilter)))
        {
            var samples = new List<QueueWait>();
            long arrivals = 0;
            double estimatedArrivals = 0;
            long missingEnqueue = 0;
            long missingDequeue = 0;
            long collisions = 0;

            foreach (var call in calls)
            {
                var enqueues = call.Events.Where(phaseEvent =>
                    phaseEvent.LogicalDirection == definition.Direction
                    && phaseEvent.Phase == definition.Enqueue).ToList();
                var dequeues = call.Events.Where(phaseEvent =>
                    phaseEvent.LogicalDirection == definition.Direction
                    && phaseEvent.Phase == definition.Dequeue).ToList();
                arrivals += enqueues.Count;
                estimatedArrivals += enqueues.Sum(static phaseEvent =>
                    phaseEvent.SelectionMode == RpcSelectionMode.DeterministicSample
                        ? Math.Max(phaseEvent.SampleRate, 1)
                        : 1);
                var usedEnqueues = new HashSet<int>();

                foreach (var dequeue in dequeues)
                {
                    var candidates = enqueues
                        .Select((phaseEvent, index) => (Event: phaseEvent, Index: index))
                        .Where(item => !usedEnqueues.Contains(item.Index)
                            && item.Event.TimestampMs <= dequeue.TimestampMs
                            && item.Event.RetryCount == dequeue.RetryCount
                            && item.Event.ForwardCount == dequeue.ForwardCount
                            && ResourceMatches(item.Event, dequeue))
                        .OrderByDescending(static item => item.Event.TimestampMs)
                        .ToList();
                    if (candidates.Count == 0)
                    {
                        missingEnqueue++;
                        continue;
                    }
                    if (candidates.Count > 1)
                    {
                        collisions += candidates.Count - 1;
                    }

                    var enqueue = candidates[0].Event;
                    usedEnqueues.Add(candidates[0].Index);
                    var waitUs = (dequeue.TimestampMs - enqueue.TimestampMs) * 1000;
                    samples.Add(new QueueWait(
                        call.Key,
                        waitUs,
                        enqueue.QueueDepth,
                        enqueue.BatchSize,
                        enqueue.BatchIndex));
                    callTotals.TryGetValue(call.Key, out var total);
                    callTotals[call.Key] = total + waitUs;
                }

                missingDequeue += enqueues.Count - usedEnqueues.Count;
            }

            if (arrivals == 0 && samples.Count == 0)
            {
                continue;
            }

            var depthValues = samples.Where(static sample => sample.Depth >= 0).Select(static sample => sample.Depth).ToArray();
            var byDepth = samples
                .Where(static sample => sample.Depth >= 0)
                .GroupBy(static sample => sample.Depth)
                .OrderBy(static group => group.Key)
                .Select(group => new QueueGroupEntry(
                    group.Key,
                    group.LongCount(),
                    RpcStatistics.Calculate(group.Select(static sample => sample.WaitUs))))
                .ToList();
            var byBatchIndex = samples
                .Where(static sample => sample.BatchIndex >= 0)
                .GroupBy(static sample => sample.BatchIndex)
                .OrderBy(static group => group.Key)
                .Select(group => new QueueGroupEntry(
                    group.Key,
                    group.LongCount(),
                    RpcStatistics.Calculate(group.Select(static sample => sample.WaitUs))))
                .ToList();
            var byBatchSize = samples
                .Where(static sample => sample.BatchSize > 0)
                .GroupBy(static sample => sample.BatchSize)
                .OrderBy(static group => group.Key)
                .Select(group => new QueueGroupEntry(
                    group.Key,
                    group.LongCount(),
                    RpcStatistics.Calculate(group.Select(static sample => sample.WaitUs))))
                .ToList();

            var fractions = new List<double>();
            var waitsForCorrelation = new List<double>();
            var endToEndForCorrelation = new List<double>();
            foreach (var callGroup in samples.GroupBy(static sample => sample.Key))
            {
                var call = callsByKey[callGroup.Key];
                if (call.EndToEndUs is not > 0)
                {
                    continue;
                }
                var wait = callGroup.Sum(static sample => sample.WaitUs);
                fractions.Add(wait / call.EndToEndUs.Value);
                waitsForCorrelation.Add(wait);
                endToEndForCorrelation.Add(call.EndToEndUs.Value);
            }

            var waitStats = RpcStatistics.Calculate(samples.Select(static sample => sample.WaitUs));
            var sampledArrivalRate = durationSeconds > 0 ? arrivals / durationSeconds : 0;
            var estimatedSourceArrivalRate = durationSeconds > 0 ? estimatedArrivals / durationSeconds : 0;
            results.Add(new QueueProfileEntry(
                definition.Name,
                definition.ResourceKind,
                arrivals,
                samples.Count,
                waitStats,
                depthValues.Length == 0 ? null : depthValues.Average(),
                depthValues.Length == 0 ? null : depthValues.Max(),
                definition.Contextual,
                byDepth,
                byBatchSize,
                byBatchIndex,
                fractions.Count == 0 ? 0 : fractions.Average(),
                RpcStatistics.Correlation(waitsForCorrelation, endToEndForCorrelation),
                sampledArrivalRate,
                estimatedSourceArrivalRate * waitStats.MeanUs / 1_000_000,
                missingEnqueue,
                missingDequeue,
                collisions,
                definition.PrecedingPhase,
                definition.FollowingPhase));

            var meanDepth = depthValues.Length == 0 ? 0 : depthValues.Average();
            var meanWaitingAhead = Math.Max(0, meanDepth - 1);
            var inferredLength = estimatedSourceArrivalRate * waitStats.MeanUs / 1_000_000;
            if (!definition.Contextual && meanWaitingAhead > 0 && inferredLength > 0
                && inferredLength / meanWaitingAhead is > 2 or < 0.5)
            {
                warnings.Add(
                    $"Queue '{definition.Name}' has L=lambda*W {inferredLength:F3} "
                    + $"versus observed mean waiting depth {meanWaitingAhead:F3}; check sampling and depth scope.");
            }

            if (missingEnqueue + missingDequeue + collisions > 0)
            {
                warnings.Add(
                    $"Queue '{definition.Name}' has {missingEnqueue} missing enqueue(s), "
                    + $"{missingDequeue} missing dequeue(s), and {collisions} resource collision(s).");
            }
        }

        return new QueueAnalysisResult(results, callTotals, warnings);
    }

    private static bool MatchesFilter(QueueDefinition definition, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || definition.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || definition.ResourceKind.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool ResourceMatches(RpcPhaseEvent enqueue, RpcPhaseEvent dequeue) =>
        enqueue.ResourceId == 0
        || dequeue.ResourceId == 0
        || (enqueue.ResourceId == dequeue.ResourceId && enqueue.ProcessId == dequeue.ProcessId);

    private sealed record QueueDefinition(
        string Name,
        string ResourceKind,
        RpcDirection Direction,
        RpcCallPhase Enqueue,
        RpcCallPhase Dequeue,
        string? PrecedingPhase,
        string? FollowingPhase,
        bool Contextual = false);

    private sealed record QueueWait(
        RpcCallKey Key,
        double WaitUs,
        int Depth,
        int BatchSize,
        int BatchIndex);
}
