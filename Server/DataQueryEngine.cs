using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Analysis;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Server;

public static class DataQueryEngine
{
    // Execute a flexible query supporting timeseries, correlate, and aggregate operations.
    public static object Execute(TraceSession session, JsonElement root)
    {
        var queryType = root.GetProperty("query").GetString()!;

        return queryType switch
        {
            "timeseries" => ExecuteTimeSeries(session, root),
            "correlate" => ExecuteCorrelation(session, root),
            "aggregate" => ExecuteAggregate(session, root),
            _ => new { error = $"Unknown query type: {queryType}" }
        };
    }

    private static object ExecuteTimeSeries(TraceSession session, JsonElement root)
    {
        double bucketMs = root.TryGetProperty("bucketMs", out var b) ? b.GetDouble() : 100;
        double fromMs = root.TryGetProperty("from", out var f) ? f.GetDouble() : 0;
        double toMs = root.TryGetProperty("to", out var t) ? t.GetDouble() : session.TraceLog.SessionDuration.TotalMilliseconds;

        int bucketCount = (int)Math.Ceiling((toMs - fromMs) / bucketMs);
        bucketCount = Math.Min(bucketCount, 10000); // Cap at 10k buckets

        var seriesArray = root.GetProperty("series");
        var results = new List<object>();

        foreach (var seriesDef in seriesArray.EnumerateArray())
        {
            var name = seriesDef.GetProperty("name").GetString()!;
            var source = seriesDef.GetProperty("source").GetString()!;
            var field = seriesDef.TryGetProperty("field", out var fld) ? fld.GetString() : "count";

            var buckets = new double[bucketCount];

            switch (source)
            {
                case "gc":
                    FillGcBuckets(session.TraceLog, seriesDef, buckets, fromMs, bucketMs, field!);
                    break;
                case "events":
                    FillEventBuckets(session.TraceLog, seriesDef, buckets, fromMs, bucketMs, field!);
                    break;
                case "exceptions":
                    FillExceptionBuckets(session.TraceLog, seriesDef, buckets, fromMs, bucketMs);
                    break;
            }

            results.Add(new
            {
                name,
                source,
                field,
                data = Enumerable.Range(0, bucketCount).Select(i => new
                {
                    timeMs = Math.Round(fromMs + i * bucketMs, 1),
                    value = Math.Round(buckets[i], 4)
                }).ToList()
            });
        }

        return new { type = "timeseries", bucketMs, fromMs, toMs, series = results };
    }

    // Correlation is timeseries with the same structure; UI handles overlay rendering.
    private static object ExecuteCorrelation(TraceSession session, JsonElement root)
    {
        return ExecuteTimeSeries(session, root);
    }

    private static object ExecuteAggregate(TraceSession session, JsonElement root)
    {
        var source = root.GetProperty("source").GetString()!;
        var groupByField = root.GetProperty("groupBy").GetString()!;
        double? fromMs = root.TryGetProperty("from", out var f) ? f.GetDouble() : null;
        double? toMs = root.TryGetProperty("to", out var t) ? t.GetDouble() : null;

        var groups = new Dictionary<string, AggregateGroup>();

        switch (source)
        {
            case "gc":
                AggregateGc(session.TraceLog, groupByField, groups, fromMs, toMs);
                break;
            case "events":
            {
                string? providerFilter = root.TryGetProperty("filter", out var filter) && filter.TryGetProperty("provider", out var p) ? p.GetString() : null;
                AggregateEvents(session.TraceLog, groupByField, providerFilter, groups, fromMs, toMs);
                break;
            }
        }

        return new
        {
            type = "aggregate",
            groupBy = groupByField,
            groups = groups.OrderByDescending(g => g.Value.Count).Select(g => new
            {
                key = g.Key,
                count = g.Value.Count,
                sum = Math.Round(g.Value.Sum, 2),
                avg = Math.Round(g.Value.Count > 0 ? g.Value.Sum / g.Value.Count : 0, 2),
                min = Math.Round(g.Value.Min, 2),
                max = Math.Round(g.Value.Max, 2)
            }).ToList()
        };
    }

    // --- Fill buckets helpers ---

    private static void FillGcBuckets(Etlx.TraceLog traceLog, JsonElement seriesDef,
        double[] buckets, double fromMs, double bucketMs, string field)
    {
        int? genFilter = null;
        if (seriesDef.TryGetProperty("filter", out var filter) && filter.TryGetProperty("generation", out var g))
            genFilter = g.GetInt32();

        using var source = traceLog.Events.GetSource();
        TraceLoadedDotNetRuntimeExtensions.NeedLoadedDotNetRuntimes(source);
        source.Process();

        foreach (var process in TraceProcessesExtensions.Processes(source))
        {
            var runtime = TraceLoadedDotNetRuntimeExtensions.LoadedDotNetRuntime(process);
            if (runtime == null) continue;

            foreach (var gc in runtime.GC.GCs)
            {
                if (genFilter.HasValue && gc.Generation != genFilter.Value) continue;

                int bucketIndex = (int)((gc.StartRelativeMSec - fromMs) / bucketMs);
                if (bucketIndex < 0 || bucketIndex >= buckets.Length) continue;

                buckets[bucketIndex] += field switch
                {
                    "pauseDurationMs" => gc.PauseDurationMSec,
                    "heapSizeAfterMB" => gc.HeapSizeAfterMB,
                    "promotedMB" => gc.PromotedMB,
                    "count" => 1,
                    _ => 1
                };
            }
        }
    }

    private static void FillEventBuckets(Etlx.TraceLog traceLog, JsonElement seriesDef,
        double[] buckets, double fromMs, double bucketMs, string field)
    {
        string? providerFilter = null, typeFilter = null;
        if (seriesDef.TryGetProperty("filter", out var filter))
        {
            if (filter.TryGetProperty("provider", out var p)) providerFilter = p.GetString();
            if (filter.TryGetProperty("type", out var t)) typeFilter = t.GetString();
        }

        foreach (var evt in traceLog.Events)
        {
            if (providerFilter != null && !evt.ProviderName.Contains(providerFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (typeFilter != null && !evt.EventName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;

            int bucketIndex = (int)((evt.TimeStampRelativeMSec - fromMs) / bucketMs);
            if (bucketIndex < 0 || bucketIndex >= buckets.Length) continue;

            if (field == "count")
            {
                buckets[bucketIndex] += 1;
            }
            else
            {
                // Try to extract a numeric payload field
                try
                {
                    var payloadNames = evt.PayloadNames;
                    for (int i = 0; i < payloadNames.Length; i++)
                    {
                        if (payloadNames[i].Equals(field, StringComparison.OrdinalIgnoreCase))
                        {
                            var val = evt.PayloadValue(i);
                            if (val != null) buckets[bucketIndex] += Convert.ToDouble(val);
                            break;
                        }
                    }
                }
                catch { buckets[bucketIndex] += 1; }
            }
        }
    }

    private static void FillExceptionBuckets(Etlx.TraceLog traceLog, JsonElement seriesDef,
        double[] buckets, double fromMs, double bucketMs)
    {
        string? typeFilter = null;
        if (seriesDef.TryGetProperty("filter", out var filter) && filter.TryGetProperty("type", out var t))
            typeFilter = t.GetString();

        foreach (var evt in traceLog.Events)
        {
            if (!evt.EventName.Contains("Exception", StringComparison.OrdinalIgnoreCase)) continue;

            if (typeFilter != null)
            {
                try
                {
                    var typeName = evt.PayloadStringByName("ExceptionType") ?? evt.PayloadStringByName("TypeName") ?? "";
                    if (!typeName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }
                catch { continue; }
            }

            int bucketIndex = (int)((evt.TimeStampRelativeMSec - fromMs) / bucketMs);
            if (bucketIndex < 0 || bucketIndex >= buckets.Length) continue;
            buckets[bucketIndex] += 1;
        }
    }

    // --- Aggregate helpers ---

    private static void AggregateGc(Etlx.TraceLog traceLog, string groupByField,
        Dictionary<string, AggregateGroup> groups, double? fromMs, double? toMs)
    {
        using var source = traceLog.Events.GetSource();
        TraceLoadedDotNetRuntimeExtensions.NeedLoadedDotNetRuntimes(source);
        source.Process();

        foreach (var process in TraceProcessesExtensions.Processes(source))
        {
            var runtime = TraceLoadedDotNetRuntimeExtensions.LoadedDotNetRuntime(process);
            if (runtime == null) continue;

            foreach (var gc in runtime.GC.GCs)
            {
                if (fromMs.HasValue && gc.StartRelativeMSec < fromMs.Value) continue;
                if (toMs.HasValue && gc.StartRelativeMSec > toMs.Value) continue;

                string key = groupByField switch
                {
                    "generation" => $"Gen {gc.Generation}",
                    "type" => gc.Type.ToString(),
                    "reason" => gc.Reason.ToString(),
                    _ => $"Gen {gc.Generation}"
                };

                if (!groups.TryGetValue(key, out var group))
                {
                    group = new AggregateGroup();
                    groups[key] = group;
                }
                group.Add(gc.PauseDurationMSec);
            }
        }
    }

    private static void AggregateEvents(Etlx.TraceLog traceLog, string groupByField,
        string? providerFilter, Dictionary<string, AggregateGroup> groups, double? fromMs, double? toMs)
    {
        foreach (var evt in traceLog.Events)
        {
            if (fromMs.HasValue && evt.TimeStampRelativeMSec < fromMs.Value) continue;
            if (toMs.HasValue && evt.TimeStampRelativeMSec > toMs.Value) continue;
            if (providerFilter != null && !evt.ProviderName.Contains(providerFilter, StringComparison.OrdinalIgnoreCase)) continue;

            string key = groupByField switch
            {
                "provider" => evt.ProviderName,
                "eventName" => evt.EventName,
                "process" => $"{evt.ProcessID}",
                _ => evt.EventName
            };

            if (!groups.TryGetValue(key, out var group))
            {
                group = new AggregateGroup();
                groups[key] = group;
            }
            group.Add(1);
        }
    }

    private class AggregateGroup
    {
        public int Count { get; private set; }
        public double Sum { get; private set; }
        public double Min { get; private set; } = double.MaxValue;
        public double Max { get; private set; } = double.MinValue;

        public void Add(double value)
        {
            Count++;
            Sum += value;
            if (value < Min) Min = value;
            if (value > Max) Max = value;
        }
    }
}
