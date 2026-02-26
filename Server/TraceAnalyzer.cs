using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Stacks;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Server;

public static class TraceAnalyzer
{
    public static TraceInfo GetTraceInfo(TraceSession session)
    {
        var traceLog = session.TraceLog;
        var processes = traceLog.Processes
            .Where(p => p.CPUMSec > 0 || p.Name != "Unknown")
            .OrderByDescending(p => p.CPUMSec)
            .Select(p => new ProcessInfo(p.ProcessID, p.Name, Math.Round(p.CPUMSec, 1)))
            .ToList();

        return new TraceInfo(
            session.Id,
            session.FilePath,
            Math.Round(traceLog.SessionDuration.TotalMilliseconds, 1),
            traceLog.EventCount,
            processes);
    }

    public static GcStatsResponse GetGcStats(Etlx.TraceLog traceLog, string? processFilter,
        bool timeline, int? longest, double? fromMs, double? toMs)
    {
        using var source = traceLog.Events.GetSource();
        TraceLoadedDotNetRuntimeExtensions.NeedLoadedDotNetRuntimes(source);
        source.Process();

        var allGcEvents = new List<GcEvent>();
        var processStats = new List<GcProcessStats>();

        foreach (var process in TraceProcessesExtensions.Processes(source))
        {
            var runtime = TraceLoadedDotNetRuntimeExtensions.LoadedDotNetRuntime(process);
            if (runtime == null || runtime.GC.Stats() == null)
                continue;

            if (processFilter != null &&
                !process.Name.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var gcStats = runtime.GC.Stats();
            int gen0 = 0, gen1 = 0, gen2 = 0;
            double filteredPauseTime = 0;
            int filteredCount = 0;

            foreach (var gc in runtime.GC.GCs)
            {
                if (fromMs.HasValue && gc.StartRelativeMSec < fromMs.Value) continue;
                if (toMs.HasValue && gc.StartRelativeMSec > toMs.Value) continue;

                filteredCount++;
                filteredPauseTime += gc.PauseDurationMSec;

                switch (gc.Generation)
                {
                    case 0: gen0++; break;
                    case 1: gen1++; break;
                    case 2: gen2++; break;
                }

                allGcEvents.Add(new GcEvent(
                    process.ProcessID, process.Name, gc.Number, gc.Generation,
                    gc.Type.ToString(), gc.Reason.ToString(),
                    Math.Round(gc.StartRelativeMSec, 3), Math.Round(gc.PauseDurationMSec, 3),
                    Math.Round(gc.HeapSizeBeforeMB, 2), Math.Round(gc.HeapSizeAfterMB, 2),
                    Math.Round(gc.PromotedMB, 2)));
            }

            if (filteredCount > 0)
            {
                processStats.Add(new GcProcessStats(
                    process.ProcessID, process.Name, filteredCount,
                    gcStats.TotalAllocatedMB, gcStats.TotalCpuMSec,
                    filteredPauseTime, gcStats.MaxSizePeakMB,
                    gcStats.GetGCPauseTimePercentage(),
                    gen0, gen1, gen2, gcStats.HeapCount));
            }
        }

        processStats = processStats.OrderByDescending(p => p.TotalPauseTimeMSec).ToList();

        List<GcEvent>? timelineEvents = null;
        if (timeline || longest.HasValue)
        {
            timelineEvents = longest.HasValue
                ? allGcEvents.OrderByDescending(e => e.PauseDurationMs).Take(longest.Value).ToList()
                : allGcEvents.OrderBy(e => e.StartTimeMs).ToList();
        }

        return new GcStatsResponse(processStats, timelineEvents);
    }

    public static JitStatsResponse GetJitStats(Etlx.TraceLog traceLog, string? processFilter)
    {
        using var source = traceLog.Events.GetSource();
        TraceLoadedDotNetRuntimeExtensions.NeedLoadedDotNetRuntimes(source);
        source.Process();

        var processes = new List<JitProcessStats>();

        foreach (var process in TraceProcessesExtensions.Processes(source))
        {
            var runtime = TraceLoadedDotNetRuntimeExtensions.LoadedDotNetRuntime(process);
            if (runtime == null || runtime.JIT.Stats() == null)
                continue;

            if (processFilter != null &&
                !process.Name.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var jitStats = runtime.JIT.Stats();
            if (!jitStats.Interesting)
                continue;

            processes.Add(new JitProcessStats(
                process.ProcessID, process.Name,
                jitStats.Count, jitStats.TotalCpuTimeMSec,
                jitStats.TotalILSize, jitStats.TotalNativeSize));
        }

        return new JitStatsResponse(processes.OrderByDescending(p => p.TotalJitCpuTimeMSec).ToList());
    }

    public static CpuStacksResponse GetCpuStacks(Etlx.TraceLog traceLog, int top,
        string groupBy, bool inclusive, double? fromMs, double? toMs)
    {
        double startMs = fromMs ?? 0;
        double endMs = toMs ?? traceLog.SessionDuration.TotalMilliseconds;
        double traceDurationMs = endMs - startMs;
        int bucketCount = Math.Min(100, Math.Max(20, (int)(traceDurationMs / 100)));

        // Walk events directly to get per-sample timing info
        Etlx.TraceEvents events;
        if (fromMs.HasValue || toMs.HasValue)
        {
            events = traceLog.Events.FilterByTime(
                traceLog.SessionStartTime + TimeSpan.FromMilliseconds(startMs),
                traceLog.SessionStartTime + TimeSpan.FromMilliseconds(endMs));
        }
        else
        {
            events = traceLog.Events;
        }

        var traceStackSource = new TraceEventStackSource(events);
        var stackSource = CopyStackSource.Clone(traceStackSource);

        // Collect per-sample method + time data
        var methodExclusive = new Dictionary<string, float>();
        var methodInclusive = new Dictionary<string, float>();
        var methodBuckets = new Dictionary<string, int[]>();
        float totalMetric = 0;

        stackSource.ForEach(delegate (StackSourceSample sample)
        {
            totalMetric += sample.Metric;
            int bucket = traceDurationMs > 0
                ? Math.Min(bucketCount - 1, Math.Max(0, (int)((sample.TimeRelativeMSec - startMs) / traceDurationMs * bucketCount)))
                : 0;

            // Walk the stack: first frame is the leaf (exclusive), rest are inclusive
            var stackIdx = sample.StackIndex;
            bool isLeaf = true;
            var seenInThisSample = new HashSet<string>();

            while (stackIdx != StackSourceCallStackIndex.Invalid)
            {
                var frameIdx = stackSource.GetFrameIndex(stackIdx);
                var frameName = stackSource.GetFrameName(frameIdx, false);
                stackIdx = stackSource.GetCallerIndex(stackIdx);

                // Skip pseudoframes: threads, processes, broken stacks
                if (IsPseudoFrame(frameName))
                {
                    isLeaf = true; // Next real frame is the actual leaf
                    continue;
                }

                var key = GetGroupKey(frameName, groupBy);

                // Exclusive: only the leaf real frame
                if (isLeaf)
                {
                    if (!methodExclusive.ContainsKey(key))
                        methodExclusive[key] = 0;
                    methodExclusive[key] += sample.Metric;
                    isLeaf = false;
                }

                // Inclusive: each unique method once per sample
                if (seenInThisSample.Add(key))
                {
                    if (!methodInclusive.ContainsKey(key))
                        methodInclusive[key] = 0;
                    methodInclusive[key] += sample.Metric;

                    if (!methodBuckets.ContainsKey(key))
                        methodBuckets[key] = new int[bucketCount];
                    methodBuckets[key][bucket]++;
                }
            }
        });

        // Build results
        var allKeys = new HashSet<string>(methodExclusive.Keys);
        allKeys.UnionWith(methodInclusive.Keys);

        var methodList = allKeys.Select(key =>
        {
            methodExclusive.TryGetValue(key, out float excl);
            methodInclusive.TryGetValue(key, out float incl);
            methodBuckets.TryGetValue(key, out int[]? buckets);
            return (Name: key, Exclusive: excl, Inclusive: incl, Buckets: buckets);
        }).ToList();

        var sorted = inclusive
            ? methodList.OrderByDescending(m => m.Inclusive).Take(top).ToList()
            : methodList.OrderByDescending(m => m.Exclusive).Take(top).ToList();

        var items = sorted.Select(m => new CpuStackEntry(
            m.Name,
            Math.Round(m.Exclusive, 2),
            Math.Round(m.Inclusive, 2),
            Math.Round(totalMetric > 0 ? (m.Exclusive / totalMetric) * 100 : 0, 2),
            m.Buckets
        )).ToList();

        return new CpuStacksResponse(
            stackSource.SampleIndexLimit,
            Math.Round(totalMetric, 2),
            groupBy,
            items,
            Math.Round(traceDurationMs, 2),
            bucketCount);
    }

    private static bool IsPseudoFrame(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.StartsWith("Thread (")) return true;
        if (name.StartsWith("Process")) return true;
        if (name == "BROKEN" || name == "?!?") return true;
        if (name.StartsWith("UNMANAGED_CODE_TIME")) return true;
        if (name.StartsWith("CPU_TIME")) return true;
        if (name.StartsWith("LAST_BLOCK")) return true;
        return false;
    }

    private static string GetGroupKey(string frameName, string groupBy)
    {
        return groupBy.ToLowerInvariant() switch
        {
            "module" => ExtractModuleFromMethod(frameName),
            "namespace" => ExtractNamespaceFromMethod(frameName),
            _ => frameName
        };
    }

    public static EventsListResponse GetEventTypeList(Etlx.TraceLog traceLog,
        string? providerFilter, double? fromMs, double? toMs)
    {
        var eventTypes = new Dictionary<string, (string Provider, string EventName, int Count)>();

        foreach (var evt in traceLog.Events)
        {
            if (fromMs.HasValue && evt.TimeStampRelativeMSec < fromMs.Value) continue;
            if (toMs.HasValue && evt.TimeStampRelativeMSec > toMs.Value) continue;

            if (providerFilter != null &&
                !evt.ProviderName.Contains(providerFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = $"{evt.ProviderName}/{evt.EventName}";
            if (eventTypes.TryGetValue(key, out var info))
                eventTypes[key] = (info.Provider, info.EventName, info.Count + 1);
            else
                eventTypes[key] = (evt.ProviderName, evt.EventName, 1);
        }

        var entries = eventTypes.Values
            .OrderByDescending(e => e.Count)
            .Select(e => new EventTypeEntry(e.Provider, e.EventName, e.Count))
            .ToList();

        return new EventsListResponse(entries);
    }

    public static EventsResponse GetEvents(Etlx.TraceLog traceLog, string? typeFilter,
        string? providerFilter, int limit, double? fromMs, double? toMs,
        int? pidFilter, int? tidFilter, string? payloadFilter)
    {
        var events = new List<TraceEventEntry>();
        int count = 0;

        foreach (var evt in traceLog.Events)
        {
            if (fromMs.HasValue && evt.TimeStampRelativeMSec < fromMs.Value) continue;
            if (toMs.HasValue && evt.TimeStampRelativeMSec > toMs.Value) continue;

            if (typeFilter != null &&
                !evt.EventName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (providerFilter != null &&
                !evt.ProviderName.Contains(providerFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (pidFilter.HasValue && evt.ProcessID != pidFilter.Value) continue;
            if (tidFilter.HasValue && evt.ThreadID != tidFilter.Value) continue;

            var message = GetEventMessage(evt);
            var payload = GetEventPayload(evt);

            if (payloadFilter != null)
            {
                bool found = message.Contains(payloadFilter, StringComparison.OrdinalIgnoreCase);
                if (!found && payload != null)
                {
                    foreach (var v in payload.Values)
                    {
                        if (v != null && v.Contains(payloadFilter, StringComparison.OrdinalIgnoreCase))
                        { found = true; break; }
                    }
                }
                if (!found) continue;
            }

            events.Add(new TraceEventEntry(
                Math.Round(evt.TimeStampRelativeMSec, 3),
                evt.ProviderName, evt.EventName,
                evt.ProcessID, evt.ThreadID,
                message, payload));

            count++;
            if (count >= limit) break;
        }

        return new EventsResponse(events);
    }

    public static ExceptionsResponse GetExceptions(Etlx.TraceLog traceLog, string? typeFilter,
        double? fromMs, double? toMs, int limit)
    {
        var exceptions = new List<ExceptionEntry>();
        var summary = new Dictionary<string, int>();

        foreach (var evt in traceLog.Events)
        {
            if (fromMs.HasValue && evt.TimeStampRelativeMSec < fromMs.Value) continue;
            if (toMs.HasValue && evt.TimeStampRelativeMSec > toMs.Value) continue;

            // Only match actual exception throw events, not EH flow events
            // CLR: "Exception/Start" has ExceptionType + ExceptionMessage payloads
            // Skip: ExceptionCatch/*, ExceptionFinally/*, ExceptionFilter/*, Exception/Stop
            if (evt.EventName == "Exception/Start" ||
                evt.EventName == "ExceptionThrown_V1" ||
                evt.EventName == "FirstChanceException")
            {
                // good — this is a real exception event
            }
            else
            {
                continue;
            }

            var exType = GetExceptionType(evt);
            var exMessage = GetExceptionMessage(evt);

            if (typeFilter != null &&
                !exType.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            summary.TryGetValue(exType, out int c);
            summary[exType] = c + 1;

            if (exceptions.Count < limit)
            {
                exceptions.Add(new ExceptionEntry(
                    Math.Round(evt.TimeStampRelativeMSec, 3),
                    exType, exMessage,
                    evt.ProcessID, evt.ThreadID));
            }
        }

        return new ExceptionsResponse(exceptions, summary);
    }

    public static AllocationsResponse GetAllocations(Etlx.TraceLog traceLog, int top,
        string groupBy, double? fromMs, double? toMs)
    {
        var allocations = new Dictionary<string, (long Count, long TotalBytes, long LargeObjectCount, long LargeObjectBytes)>();
        long totalBytes = 0;
        long totalCount = 0;

        foreach (var evt in traceLog.Events)
        {
            if (evt.EventName != "GC/AllocationTick" && evt.EventName != "GC/SampledObjectAllocation") continue;

            double timeMs = evt.TimeStampRelativeMSec;
            if (fromMs.HasValue && timeMs < fromMs.Value) continue;
            if (toMs.HasValue && timeMs > toMs.Value) continue;

            string? typeName = null;
            long size = 0;
            bool isLargeObject = false;

            try
            {
                typeName = evt.PayloadStringByName("TypeName");

                if (evt.EventName == "GC/AllocationTick")
                {
                    var amount64 = evt.PayloadByName("AllocationAmount64");
                    if (amount64 != null)
                        size = Convert.ToInt64(amount64);
                    else
                    {
                        var amount = evt.PayloadByName("AllocationAmount");
                        if (amount != null)
                            size = Convert.ToInt64(amount);
                    }
                    var kind = evt.PayloadByName("AllocationKind");
                    if (kind != null)
                        isLargeObject = Convert.ToInt32(kind) == 1;
                }
                else
                {
                    var totalSize = evt.PayloadByName("TotalSizeForTypeSample");
                    if (totalSize != null)
                        size = Convert.ToInt64(totalSize);
                    isLargeObject = size > 85000;
                }
            }
            catch { continue; }

            if (string.IsNullOrEmpty(typeName) || size <= 0) continue;

            string key = groupBy.ToLowerInvariant() switch
            {
                "namespace" => ExtractNamespace(typeName),
                "module" => ExtractModule(typeName),
                _ => typeName
            };

            if (!allocations.TryGetValue(key, out var info))
                info = (0, 0, 0, 0);

            allocations[key] = (
                info.Count + 1,
                info.TotalBytes + size,
                info.LargeObjectCount + (isLargeObject ? 1 : 0),
                info.LargeObjectBytes + (isLargeObject ? size : 0));

            totalBytes += size;
            totalCount++;
        }

        var sorted = allocations
            .OrderByDescending(a => a.Value.TotalBytes)
            .Take(top)
            .Select(a => new AllocationEntry(
                a.Key, a.Value.Count, a.Value.TotalBytes,
                a.Value.Count > 0 ? Math.Round((double)a.Value.TotalBytes / a.Value.Count, 2) : 0,
                a.Value.LargeObjectCount, a.Value.LargeObjectBytes))
            .ToList();

        return new AllocationsResponse(totalCount, totalBytes, groupBy, sorted);
    }

    // --- Helper methods ---

    private static string ExtractModuleFromMethod(string methodName)
    {
        if (string.IsNullOrEmpty(methodName)) return "[Unknown]";
        if (methodName.StartsWith("Thread (") || methodName.StartsWith("Process")) return "[Runtime]";
        if (methodName == "?!?") return "[Native/Unknown]";
        var bangIndex = methodName.IndexOf('!');
        return bangIndex > 0 ? methodName.Substring(0, bangIndex) : "[Unknown]";
    }

    private static string ExtractNamespaceFromMethod(string methodName)
    {
        if (string.IsNullOrEmpty(methodName)) return "[Unknown]";
        if (methodName.StartsWith("Thread (") || methodName.StartsWith("Process")) return "[Runtime]";
        if (methodName == "?!?") return "[Native/Unknown]";
        var bangIndex = methodName.IndexOf('!');
        if (bangIndex < 0) return "[Unknown]";
        var fullName = methodName.Substring(bangIndex + 1);
        var parenIndex = fullName.IndexOf('(');
        if (parenIndex > 0) fullName = fullName.Substring(0, parenIndex);
        var parts = fullName.Split('.');
        if (parts.Length <= 2) return parts[0];
        return string.Join(".", parts.Take(parts.Length - 2));
    }

    private static string ExtractNamespace(string typeName)
    {
        int genericIdx = typeName.IndexOf('`');
        if (genericIdx > 0) typeName = typeName.Substring(0, genericIdx);
        int bracketIdx = typeName.IndexOf('[');
        if (bracketIdx > 0) typeName = typeName.Substring(0, bracketIdx);
        int lastDot = typeName.LastIndexOf('.');
        return lastDot > 0 ? typeName.Substring(0, lastDot) : typeName;
    }

    private static string ExtractModule(string typeName)
    {
        int firstDot = typeName.IndexOf('.');
        return firstDot > 0 ? typeName.Substring(0, firstDot) : typeName;
    }

    private static string GetEventMessage(TraceEvent evt)
    {
        try
        {
            var payloadNames = evt.PayloadNames;
            if (payloadNames.Length == 0) return "";
            var parts = new List<string>();
            for (int i = 0; i < payloadNames.Length; i++)
            {
                var name = payloadNames[i];
                var value = evt.PayloadValue(i);
                if (value != null)
                {
                    var valueStr = value.ToString();
                    if (!string.IsNullOrEmpty(valueStr))
                        parts.Add($"{name}={valueStr}");
                }
            }
            return string.Join(", ", parts);
        }
        catch { return ""; }
    }

    private static Dictionary<string, string>? GetEventPayload(TraceEvent evt)
    {
        try
        {
            var payloadNames = evt.PayloadNames;
            if (payloadNames.Length == 0) return null;
            var payload = new Dictionary<string, string>();
            for (int i = 0; i < payloadNames.Length; i++)
            {
                var value = evt.PayloadValue(i)?.ToString();
                if (value != null)
                    payload[payloadNames[i]] = value;
            }
            return payload.Count > 0 ? payload : null;
        }
        catch { return null; }
    }

    private static string GetExceptionType(TraceEvent evt)
    {
        var payloadNames = evt.PayloadNames;
        // First pass: look for ExceptionType specifically (CLR Exception/Start event)
        for (int i = 0; i < payloadNames.Length; i++)
        {
            if (string.Equals(payloadNames[i], "ExceptionType", StringComparison.OrdinalIgnoreCase))
            {
                var value = evt.PayloadValue(i)?.ToString();
                if (!string.IsNullOrEmpty(value)) return value!;
            }
        }
        // Second pass: look for any payload containing a type name
        for (int i = 0; i < payloadNames.Length; i++)
        {
            var name = payloadNames[i].ToLower();
            if (name.Contains("type") || name.Contains("name"))
            {
                var value = evt.PayloadValue(i)?.ToString();
                if (!string.IsNullOrEmpty(value)) return value!;
            }
        }
        return "Unknown";
    }

    private static string GetExceptionMessage(TraceEvent evt)
    {
        var payloadNames = evt.PayloadNames;
        for (int i = 0; i < payloadNames.Length; i++)
        {
            if (payloadNames[i].ToLower().Contains("message"))
                return evt.PayloadValue(i)?.ToString() ?? "";
        }
        return "";
    }

    // --- Call Tree ---

    private static List<CallTreeNode> GetRealChildren(CallTreeNode node)
    {
        // Skip pseudo-frames (Thread, Process, etc.) and collect real children
        var result = new List<CallTreeNode>();
        CollectRealChildren(node, result);
        return result;
    }

    private static void CollectRealChildren(CallTreeNode node, List<CallTreeNode> result)
    {
        if (node.Callees == null) return;
        foreach (var child in node.Callees)
        {
            if (IsPseudoFrame(child.Name))
            {
                // Recurse through pseudo-frame to find real children
                CollectRealChildren(child, result);
            }
            else
            {
                result.Add(child);
            }
        }
    }

    private static CallTreeNodeDto SerializeNode(CallTreeNodeBase node, float percentBasis,
        int depth, int maxDepth, bool skipPseudo = true)
    {
        int childCount = 0;
        List<CallTreeNodeDto>? children = null;

        if (node is CallTreeNode treeNode)
        {
            var realChildren = skipPseudo ? GetRealChildren(treeNode) : (IList<CallTreeNode>)treeNode.Callees;
            childCount = realChildren?.Count ?? 0;

            if (depth < maxDepth && realChildren != null && realChildren.Count > 0)
            {
                children = realChildren
                    .OrderByDescending(c => Math.Abs(c.InclusiveMetric))
                    .Select(c => SerializeNode(c, percentBasis, depth + 1, maxDepth, skipPseudo))
                    .ToList();
            }
        }

        return new CallTreeNodeDto(
            node.Name,
            Math.Round(node.InclusiveMetric, 2),
            Math.Round(node.ExclusiveMetric, 2),
            Math.Round(percentBasis > 0 ? node.InclusiveMetric * 100 / percentBasis : 0, 2),
            Math.Round(percentBasis > 0 ? node.ExclusiveMetric * 100 / percentBasis : 0, 2),
            childCount,
            children
        );
    }

    public static CallTreeResponse GetCallTree(CallTree callTree, int depth)
    {
        var root = callTree.Root;
        var realChildren = GetRealChildren(root);

        var nodes = realChildren
            .OrderByDescending(c => Math.Abs(c.InclusiveMetric))
            .Select(c => SerializeNode(c, callTree.PercentageBasis, 1, depth))
            .ToList();

        return new CallTreeResponse(
            Math.Round(root.InclusiveMetric, 2),
            (int)root.InclusiveCount,
            nodes
        );
    }

    public static CallTreeResponse GetCallTreeChildren(CallTree callTree, int[] path, int depth)
    {
        // Navigate to the node at the given path
        CallTreeNode current = callTree.Root;
        foreach (int idx in path)
        {
            var children = GetRealChildren(current)
                .OrderByDescending(c => Math.Abs(c.InclusiveMetric))
                .ToList();

            if (idx < 0 || idx >= children.Count)
                return new CallTreeResponse(0, 0, new List<CallTreeNodeDto>());

            current = children[idx];
        }

        var realChildren = GetRealChildren(current);
        var nodes = realChildren
            .OrderByDescending(c => Math.Abs(c.InclusiveMetric))
            .Select(c => SerializeNode(c, callTree.PercentageBasis, 1, depth))
            .ToList();

        return new CallTreeResponse(
            Math.Round(callTree.Root.InclusiveMetric, 2),
            (int)callTree.Root.InclusiveCount,
            nodes
        );
    }

    public static CallerCalleeResponse GetCallerCallee(CallTree callTree, string methodName)
    {
        // Try exact match first; if no results, find best substring match by inclusive metric
        var callerCallee = callTree.CallerCallee(methodName);
        if (callerCallee.InclusiveMetric == 0 && callerCallee.Callers.Count() == 0 && callerCallee.Callees.Count() == 0)
        {
            var bestMatch = callTree.ByID
                .Where(n => n.Name.Contains(methodName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => Math.Abs(n.InclusiveMetric))
                .FirstOrDefault();
            if (bestMatch != null)
                callerCallee = callTree.CallerCallee(bestMatch.Name);
        }

        var focus = new CallTreeNodeDto(
            callerCallee.Name,
            Math.Round(callerCallee.InclusiveMetric, 2),
            Math.Round(callerCallee.ExclusiveMetric, 2),
            Math.Round(callerCallee.InclusiveMetricPercent, 2),
            Math.Round(callerCallee.ExclusiveMetricPercent, 2),
            0, null
        );

        var callers = callerCallee.Callers
            .OrderByDescending(c => Math.Abs(c.InclusiveMetric))
            .Select(c => new CallTreeNodeDto(
                c.Name,
                Math.Round(c.InclusiveMetric, 2),
                Math.Round(c.ExclusiveMetric, 2),
                Math.Round(c.InclusiveMetricPercent, 2),
                Math.Round(c.ExclusiveMetricPercent, 2),
                0, null
            ))
            .ToList();

        var callees = callerCallee.Callees
            .OrderByDescending(c => Math.Abs(c.InclusiveMetric))
            .Select(c => new CallTreeNodeDto(
                c.Name,
                Math.Round(c.InclusiveMetric, 2),
                Math.Round(c.ExclusiveMetric, 2),
                Math.Round(c.InclusiveMetricPercent, 2),
                Math.Round(c.ExclusiveMetricPercent, 2),
                0, null
            ))
            .ToList();

        return new CallerCalleeResponse(focus, callers, callees);
    }

    public static CallTreeResponse GetHotPath(CallTree callTree, int[] startPath)
    {
        // Navigate to starting node
        CallTreeNode current = callTree.Root;
        foreach (int idx in startPath)
        {
            var children = GetRealChildren(current)
                .OrderByDescending(c => Math.Abs(c.InclusiveMetric))
                .ToList();

            if (idx < 0 || idx >= children.Count)
                return new CallTreeResponse(0, 0, new List<CallTreeNodeDto>());

            current = children[idx];
        }

        // Serialize with hot-path-aware expansion
        var nodes = SerializeHotPathChildren(current, callTree.PercentageBasis);

        return new CallTreeResponse(
            Math.Round(callTree.Root.InclusiveMetric, 2),
            (int)callTree.Root.InclusiveCount,
            nodes
        );
    }

    private static List<CallTreeNodeDto> SerializeHotPathChildren(CallTreeNode node, float percentBasis, int depth = 0)
    {
        var realChildren = GetRealChildren(node);
        if (realChildren == null || realChildren.Count == 0)
            return new List<CallTreeNodeDto>();

        var sorted = realChildren
            .OrderByDescending(c => Math.Abs(c.InclusiveMetric))
            .ToList();

        var topChild = sorted[0];
        // Continue hot path if top child carries >= 80% of parent's inclusive metric
        // Limit depth to 30 to stay within JSON serializer's max depth of 64
        bool continueHotPath = depth < 30
            && sorted.Count > 0
            && Math.Abs(node.InclusiveMetric) > 0
            && Math.Abs(topChild.InclusiveMetric) >= Math.Abs(node.InclusiveMetric) * 0.8;

        return sorted.Select((child, i) =>
        {
            List<CallTreeNodeDto>? children = null;
            if (i == 0 && continueHotPath)
            {
                children = SerializeHotPathChildren(child, percentBasis, depth + 1);
            }

            var rc = GetRealChildren(child);
            return new CallTreeNodeDto(
                child.Name,
                Math.Round(child.InclusiveMetric, 2),
                Math.Round(child.ExclusiveMetric, 2),
                Math.Round(percentBasis > 0 ? child.InclusiveMetric * 100 / percentBasis : 0, 2),
                Math.Round(percentBasis > 0 ? child.ExclusiveMetric * 100 / percentBasis : 0, 2),
                rc?.Count ?? 0,
                children
            );
        }).ToList();
    }

    // ── Timeline Correlation ──────────────────────────────────────────

    public static TimelineResponse GetTimeline(Etlx.TraceLog traceLog, double? fromMs, double? toMs,
        int bucketCount, HashSet<string> lanes)
    {
        double startMs = fromMs ?? 0;
        double endMs = toMs ?? traceLog.SessionDuration.TotalMilliseconds;
        bucketCount = Math.Max(5, Math.Min(200, bucketCount));
        double bucketSize = (endMs - startMs) / bucketCount;

        var result = new Dictionary<string, object[]>();

        if (lanes.Contains("gc"))
            result["gc"] = BuildGcLane(traceLog, startMs, endMs, bucketCount, bucketSize);

        if (lanes.Contains("cpu"))
            result["cpu"] = BuildCpuLane(traceLog, startMs, endMs, bucketCount, bucketSize);

        if (lanes.Contains("exceptions"))
            result["exceptions"] = BuildExceptionLane(traceLog, startMs, endMs, bucketCount, bucketSize);

        if (lanes.Contains("alloc"))
            result["alloc"] = BuildAllocLane(traceLog, startMs, endMs, bucketCount, bucketSize);

        if (lanes.Contains("jit"))
            result["jit"] = BuildJitLane(traceLog, startMs, endMs, bucketCount, bucketSize);

        if (lanes.Contains("events"))
            result["events"] = BuildEventLane(traceLog, startMs, endMs, bucketCount, bucketSize);

        return new TimelineResponse(
            Math.Round(startMs, 1), Math.Round(endMs, 1),
            Math.Round(bucketSize, 1), bucketCount, result);
    }

    private static object[] BuildGcLane(Etlx.TraceLog traceLog, double startMs, double endMs,
        int bucketCount, double bucketSize)
    {
        var buckets = new GcBucket[bucketCount];
        for (int i = 0; i < bucketCount; i++)
            buckets[i] = new GcBucket(0, 0, 0, false);

        using var source = traceLog.Events.GetSource();
        TraceLoadedDotNetRuntimeExtensions.NeedLoadedDotNetRuntimes(source);
        source.Process();

        foreach (var process in TraceProcessesExtensions.Processes(source))
        {
            var runtime = TraceLoadedDotNetRuntimeExtensions.LoadedDotNetRuntime(process);
            if (runtime?.GC == null) continue;

            foreach (var gc in runtime.GC.GCs)
            {
                if (gc.StartRelativeMSec < startMs || gc.StartRelativeMSec > endMs) continue;
                int idx = Math.Min(bucketCount - 1, Math.Max(0, (int)((gc.StartRelativeMSec - startMs) / bucketSize)));
                var b = buckets[idx];
                buckets[idx] = new GcBucket(
                    b.GcCount + 1,
                    Math.Round(b.TotalPauseMs + gc.PauseDurationMSec, 2),
                    Math.Round(Math.Max(b.MaxPauseMs, gc.PauseDurationMSec), 2),
                    b.HasGen2 || gc.Generation >= 2);
            }
        }
        return buckets.Cast<object>().ToArray();
    }

    private static object[] BuildCpuLane(Etlx.TraceLog traceLog, double startMs, double endMs,
        int bucketCount, double bucketSize)
    {
        var sampleCounts = new int[bucketCount];
        var topMethods = new Dictionary<string, int>[bucketCount];
        for (int i = 0; i < bucketCount; i++)
            topMethods[i] = new Dictionary<string, int>();

        var events = traceLog.Events.FilterByTime(
            traceLog.SessionStartTime + TimeSpan.FromMilliseconds(startMs),
            traceLog.SessionStartTime + TimeSpan.FromMilliseconds(endMs));
        var traceStackSource = new TraceEventStackSource(events);
        var stackSource = CopyStackSource.Clone(traceStackSource);

        stackSource.ForEach(delegate (StackSourceSample sample)
        {
            int idx = Math.Min(bucketCount - 1, Math.Max(0, (int)((sample.TimeRelativeMSec - startMs) / bucketSize)));
            sampleCounts[idx]++;

            // Find leaf method
            var stackIdx = sample.StackIndex;
            while (stackIdx != StackSourceCallStackIndex.Invalid)
            {
                var frameIdx = stackSource.GetFrameIndex(stackIdx);
                var name = stackSource.GetFrameName(frameIdx, false);
                stackIdx = stackSource.GetCallerIndex(stackIdx);
                if (IsPseudoFrame(name)) continue;
                topMethods[idx].TryGetValue(name, out int c);
                topMethods[idx][name] = c + 1;
                break; // only leaf
            }
        });

        var result = new CpuBucket[bucketCount];
        for (int i = 0; i < bucketCount; i++)
        {
            string? top = topMethods[i].Count > 0
                ? topMethods[i].OrderByDescending(kv => kv.Value).First().Key
                : null;
            result[i] = new CpuBucket(sampleCounts[i], top);
        }
        return result.Cast<object>().ToArray();
    }

    private static object[] BuildExceptionLane(Etlx.TraceLog traceLog, double startMs, double endMs,
        int bucketCount, double bucketSize)
    {
        var counts = new int[bucketCount];
        var topTypes = new Dictionary<string, int>[bucketCount];
        for (int i = 0; i < bucketCount; i++)
            topTypes[i] = new Dictionary<string, int>();

        foreach (var evt in traceLog.Events)
        {
            if (evt.TimeStampRelativeMSec < startMs || evt.TimeStampRelativeMSec > endMs) continue;
            if (evt.EventName != "Exception/Start" && evt.EventName != "ExceptionThrown_V1"
                && evt.EventName != "FirstChanceException") continue;

            int idx = Math.Min(bucketCount - 1, Math.Max(0, (int)((evt.TimeStampRelativeMSec - startMs) / bucketSize)));
            counts[idx]++;
            var exType = GetExceptionType(evt);
            topTypes[idx].TryGetValue(exType, out int c);
            topTypes[idx][exType] = c + 1;
        }

        var result = new ExceptionBucket[bucketCount];
        for (int i = 0; i < bucketCount; i++)
        {
            string? top = topTypes[i].Count > 0
                ? topTypes[i].OrderByDescending(kv => kv.Value).First().Key
                : null;
            result[i] = new ExceptionBucket(counts[i], top);
        }
        return result.Cast<object>().ToArray();
    }

    private static object[] BuildAllocLane(Etlx.TraceLog traceLog, double startMs, double endMs,
        int bucketCount, double bucketSize)
    {
        var allocCounts = new long[bucketCount];
        var allocBytes = new long[bucketCount];

        foreach (var evt in traceLog.Events)
        {
            if (evt.EventName != "GC/AllocationTick" && evt.EventName != "GC/SampledObjectAllocation") continue;
            if (evt.TimeStampRelativeMSec < startMs || evt.TimeStampRelativeMSec > endMs) continue;

            int idx = Math.Min(bucketCount - 1, Math.Max(0, (int)((evt.TimeStampRelativeMSec - startMs) / bucketSize)));
            allocCounts[idx]++;
            try
            {
                long size = 0;
                if (evt.EventName == "GC/AllocationTick")
                {
                    var amount64 = evt.PayloadByName("AllocationAmount64");
                    if (amount64 != null) size = Convert.ToInt64(amount64);
                    else
                    {
                        var amount = evt.PayloadByName("AllocationAmount");
                        if (amount != null) size = Convert.ToInt64(amount);
                    }
                }
                else
                {
                    var totalSize = evt.PayloadByName("TotalSizeForTypeSample");
                    if (totalSize != null) size = Convert.ToInt64(totalSize);
                }
                allocBytes[idx] += size;
            }
            catch { }
        }

        var result = new AllocBucket[bucketCount];
        for (int i = 0; i < bucketCount; i++)
            result[i] = new AllocBucket(allocCounts[i], allocBytes[i]);
        return result.Cast<object>().ToArray();
    }

    private static object[] BuildJitLane(Etlx.TraceLog traceLog, double startMs, double endMs,
        int bucketCount, double bucketSize)
    {
        var methodCounts = new int[bucketCount];
        var totalMs = new double[bucketCount];

        foreach (var evt in traceLog.Events)
        {
            if (evt.EventName != "Method/JittingStarted" && evt.EventName != "MethodJittingStarted") continue;
            if (evt.TimeStampRelativeMSec < startMs || evt.TimeStampRelativeMSec > endMs) continue;

            int idx = Math.Min(bucketCount - 1, Math.Max(0, (int)((evt.TimeStampRelativeMSec - startMs) / bucketSize)));
            methodCounts[idx]++;
        }

        var result = new JitBucket[bucketCount];
        for (int i = 0; i < bucketCount; i++)
            result[i] = new JitBucket(methodCounts[i], Math.Round(totalMs[i], 2));
        return result.Cast<object>().ToArray();
    }

    private static object[] BuildEventLane(Etlx.TraceLog traceLog, double startMs, double endMs,
        int bucketCount, double bucketSize)
    {
        var counts = new int[bucketCount];

        foreach (var evt in traceLog.Events)
        {
            if (evt.TimeStampRelativeMSec < startMs || evt.TimeStampRelativeMSec > endMs) continue;
            int idx = Math.Min(bucketCount - 1, Math.Max(0, (int)((evt.TimeStampRelativeMSec - startMs) / bucketSize)));
            counts[idx]++;
        }

        var result = new EventBucket[bucketCount];
        for (int i = 0; i < bucketCount; i++)
            result[i] = new EventBucket(counts[i]);
        return result.Cast<object>().ToArray();
    }

    // ── Point-in-Time Snapshot ────────────────────────────────────────

    public static SnapshotResponse GetSnapshot(Etlx.TraceLog traceLog, double atMs, double windowMs)
    {
        double from = Math.Max(0, atMs - windowMs);
        double to = Math.Min(traceLog.SessionDuration.TotalMilliseconds, atMs + windowMs);

        // GC events in window
        SnapshotGc? gcSnapshot = null;
        {
            var gcResponse = GetGcStats(traceLog, null, true, null, from, to);
            if (gcResponse.Timeline != null && gcResponse.Timeline.Count > 0)
                gcSnapshot = new SnapshotGc(gcResponse.Timeline.Count, gcResponse.Timeline);
        }

        // CPU top methods in window
        SnapshotCpu? cpuSnapshot = null;
        {
            var cpuResponse = GetCpuStacks(traceLog, 5, "method", false, from, to);
            if (cpuResponse.TotalSamples > 0)
            {
                var methods = cpuResponse.Items.Select(i =>
                    new SnapshotCpuMethod(i.Name, i.ExclusiveMs, i.ExclusivePercent)).ToList();
                cpuSnapshot = new SnapshotCpu(cpuResponse.TotalSamples, methods);
            }
        }

        // Exceptions in window
        SnapshotExceptions? exSnapshot = null;
        {
            var exResponse = GetExceptions(traceLog, null, from, to, 10);
            if (exResponse.Exceptions.Count > 0)
                exSnapshot = new SnapshotExceptions(
                    exResponse.Summary.Values.Sum(), exResponse.Exceptions);
        }

        // Event type counts in window
        SnapshotEvents? evtSnapshot = null;
        {
            var evtResponse = GetEventTypeList(traceLog, null, from, to);
            if (evtResponse.EventTypes.Count > 0)
            {
                var summaries = evtResponse.EventTypes
                    .OrderByDescending(e => e.Count)
                    .Take(15)
                    .Select(e => new SnapshotEventSummary(e.Provider, e.EventName, e.Count))
                    .ToList();
                evtSnapshot = new SnapshotEvents(
                    evtResponse.EventTypes.Sum(e => e.Count), summaries);
            }
        }

        return new SnapshotResponse(
            Math.Round(atMs, 1), Math.Round(from, 1), Math.Round(to, 1),
            gcSnapshot, cpuSnapshot, exSnapshot, evtSnapshot);
    }
}
