using System.ComponentModel;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace PVAnalyze.Server;

public class CopilotService : IAsyncDisposable
{
    private CopilotClient? _client;
    private readonly TraceSessionManager _sessionManager;
    private readonly Dictionary<string, CopilotSession> _sessions = new();

    public CopilotService(TraceSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public async Task EnsureStartedAsync()
    {
        if (_client != null) return;
        _client = new CopilotClient(new CopilotClientOptions
        {
            AutoStart = true,
            UseStdio = false,
        });
        await _client.StartAsync();
    }

    public async Task ChatStreamAsync(string traceId, string message, HttpResponse httpResponse, CancellationToken ct = default)
    {
        var traceSession = _sessionManager.GetSession(traceId);
        if (traceSession == null)
            throw new InvalidOperationException($"Trace session '{traceId}' not found");

        try
        {
            await EnsureStartedAsync();

            if (!_sessions.TryGetValue(traceId, out var session))
            {
                session = await CreateSessionForTrace(traceId, traceSession);
                _sessions[traceId] = session;
            }

            httpResponse.ContentType = "text/event-stream";
            httpResponse.Headers.CacheControl = "no-cache";
            httpResponse.Headers.Connection = "keep-alive";
            await httpResponse.Body.FlushAsync(ct);

            // SessionEventHandler is a synchronous delegate (void), so we
            // queue SSE writes into a channel and drain them on the request thread.
            var writeQueue = System.Threading.Channels.Channel.CreateUnbounded<(string type, string data)>();
            var tcs = new TaskCompletionSource();
            var fullContent = new System.Text.StringBuilder();

            using var sub = session.On(evt =>
            {
                try
                {
                    switch (evt)
                    {
                        case AssistantMessageDeltaEvent delta:
                            var chunk = delta.Data.DeltaContent;
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                fullContent.Append(chunk);
                                var escaped = JsonSerializer.Serialize(chunk);
                                writeQueue.Writer.TryWrite(("delta", $"{{\"content\":{escaped}}}"));
                            }
                            break;
                        case AssistantMessageEvent msg:
                            // Multiple AssistantMessageEvents fire per turn (e.g. tool-calling
                            // messages with empty content). Track the last one with real content.
                            if (!string.IsNullOrEmpty(msg.Data.Content))
                            {
                                // Non-streaming final content — replace accumulated deltas
                                fullContent.Clear();
                                fullContent.Append(msg.Data.Content);
                            }
                            break;
                        case ToolExecutionStartEvent toolStart:
                            var toolName = JsonSerializer.Serialize(toolStart.Data.ToolName ?? "");
                            writeQueue.Writer.TryWrite(("tool", $"{{\"name\":{toolName},\"status\":\"start\"}}"));
                            break;
                        case ToolExecutionCompleteEvent toolEnd:
                            writeQueue.Writer.TryWrite(("tool", "{\"status\":\"complete\"}"));
                            break;
                        case SessionIdleEvent:
                            // Session is done — send final content
                            var content = fullContent.ToString();
                            var serialized = JsonSerializer.Serialize(content);
                            writeQueue.Writer.TryWrite(("done", $"{{\"content\":{serialized}}}"));
                            tcs.TrySetResult();
                            break;
                        case SessionErrorEvent err:
                            var errMsg = err.Data?.Message ?? "Unknown error";
                            writeQueue.Writer.TryWrite(("error", $"{{\"error\":{JsonSerializer.Serialize(errMsg)}}}"));
                            tcs.TrySetResult();
                            break;
                    }
                }
                catch { /* ignore */ }
            });

            await session.SendAsync(new MessageOptions { Prompt = message });

            // Drain write queue until done or timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));

            _ = tcs.Task.ContinueWith(_ =>
            {
                // Give a brief moment for any final queued items, then complete the channel
                Task.Delay(200).ContinueWith(_ => writeQueue.Writer.TryComplete());
            });

            try
            {
                await foreach (var (type, data) in writeQueue.Reader.ReadAllAsync(cts.Token))
                {
                    await WriteSSE(httpResponse, type, data, ct);
                }
            }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CopilotService] ChatStreamAsync error: {ex}");
            try { await WriteSSE(httpResponse, "error", $"{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}}", ct); }
            catch { }
        }
    }

    private static async Task WriteSSE(HttpResponse response, string eventType, string data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventType}\ndata: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    public async Task<ChatResponse> ChatAsync(string traceId, string message, CancellationToken ct = default)
    {
        var traceSession = _sessionManager.GetSession(traceId);
        if (traceSession == null)
            throw new InvalidOperationException($"Trace session '{traceId}' not found");

        try
        {
            await EnsureStartedAsync();

            if (!_sessions.TryGetValue(traceId, out var session))
            {
                session = await CreateSessionForTrace(traceId, traceSession);
                _sessions[traceId] = session;
            }

            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = message },
                TimeSpan.FromMinutes(2));

            return new ChatResponse
            {
                Content = response?.Data.Content ?? "No response received.",
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CopilotService] ChatAsync error: {ex}");
            throw;
        }
    }

    private async Task<CopilotSession> CreateSessionForTrace(string traceId, TraceSession traceSession)
    {
        // Build trace context summary
        var info = TraceAnalyzer.GetTraceInfo(traceSession);
        var contextSummary = $@"You are analyzing a .NET performance trace.
Trace file: {traceSession.FilePath}
Duration: {info.DurationMSec:F0}ms ({info.DurationMSec / 1000:F1}s)
Events: {info.EventCount:N0}
Processes: {string.Join(", ", info.Processes.Select(p => $"{p.Name} (PID {p.ProcessId}, CPU {p.CpuMSec:F0}ms)"))}

You have tools to query this trace. Use them to answer the user's questions about performance.
When the user asks about performance issues, use the tools to gather data before answering.
Present findings clearly with specific numbers, percentages, and method names.
If you generate a visualization spec, return it as a JSON code block tagged with ```vizspec.";

        var tools = BuildTraceTools(traceId, traceSession);

        var session = await _client!.CreateSessionAsync(new SessionConfig
        {
            Model = "claude-sonnet-4",
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = contextSummary,
            },
            Tools = tools,
        });

        return session;
    }

    private ICollection<AIFunction> BuildTraceTools(string traceId, TraceSession traceSession)
    {
        var tools = new List<AIFunction>();

        tools.Add(AIFunctionFactory.Create(
            ([Description("Number of top methods to return")] int top,
             [Description("Group by: method, module, or namespace")] string? groupBy,
             [Description("Sort by inclusive time instead of exclusive")] bool? inclusive) =>
            {
                var result = TraceAnalyzer.GetCpuStacks(traceSession.TraceLog,
                    top, groupBy ?? "method", inclusive ?? false, null, null);
                return JsonSerializer.Serialize(result);
            },
            "get_cpu_stacks",
            "Get top CPU-consuming methods with inclusive/exclusive time. Use to find where CPU time is spent."));

        tools.Add(AIFunctionFactory.Create(
            () =>
            {
                var result = TraceAnalyzer.GetGcStats(traceSession.TraceLog, null, true, null, null, null);
                return JsonSerializer.Serialize(result);
            },
            "get_gc_stats",
            "Get garbage collection statistics including timeline of all GC events, pause times, generations, and heap sizes."));

        tools.Add(AIFunctionFactory.Create(
            () =>
            {
                var result = TraceAnalyzer.GetJitStats(traceSession.TraceLog, null);
                return JsonSerializer.Serialize(result);
            },
            "get_jit_stats",
            "Get JIT compilation statistics including methods compiled, time spent, and tier breakdown."));

        tools.Add(AIFunctionFactory.Create(
            ([Description("Max tree depth")] int depth) =>
            {
                var callTree = traceSession.GetOrBuildCallTree();
                var result = TraceAnalyzer.GetCallTree(callTree, depth);
                return JsonSerializer.Serialize(result);
            },
            "get_call_tree",
            "Get CPU call tree showing method hierarchy with inclusive/exclusive percentages."));

        tools.Add(AIFunctionFactory.Create(
            () =>
            {
                var callTree = traceSession.GetOrBuildCallTree();
                var result = TraceAnalyzer.GetHotPath(callTree, new[] { 0 });
                return JsonSerializer.Serialize(result);
            },
            "get_hot_path",
            "Get the hot path - the dominant call chain where most CPU time is spent."));

        tools.Add(AIFunctionFactory.Create(
            ([Description("Method name or substring to look up")] string method) =>
            {
                var callTree = traceSession.GetOrBuildCallTree();
                var result = TraceAnalyzer.GetCallerCallee(callTree, method);
                return JsonSerializer.Serialize(result);
            },
            "get_caller_callee",
            "Get callers and callees for a specific method. Supports substring matching."));

        tools.Add(AIFunctionFactory.Create(
            ([Description("Filter by event type name")] string? type,
             [Description("Filter by provider name")] string? provider,
             [Description("Max events to return")] int? limit) =>
            {
                var result = TraceAnalyzer.GetEvents(traceSession.TraceLog, type, provider,
                    limit ?? 50, null, null, null, null, null);
                return JsonSerializer.Serialize(result);
            },
            "get_events",
            "Get trace events filtered by type or provider. Use to find specific runtime events."));

        tools.Add(AIFunctionFactory.Create(
            () =>
            {
                var result = TraceAnalyzer.GetEventTypeList(traceSession.TraceLog, null, null, null);
                return JsonSerializer.Serialize(result);
            },
            "list_event_types",
            "List all unique event types in the trace with counts. Use to discover what data is available."));

        tools.Add(AIFunctionFactory.Create(
            ([Description("Filter by exception type")] string? type) =>
            {
                var result = TraceAnalyzer.GetExceptions(traceSession.TraceLog, type, null, null, 50);
                return JsonSerializer.Serialize(result);
            },
            "get_exceptions",
            "Get exceptions thrown during the trace, grouped by type with messages and stack traces."));

        tools.Add(AIFunctionFactory.Create(
            ([Description("Number of top types to return")] int? top,
             [Description("Group by: type, namespace, or module")] string? groupBy) =>
            {
                var result = TraceAnalyzer.GetAllocations(traceSession.TraceLog,
                    top ?? 20, groupBy ?? "type", null, null);
                return JsonSerializer.Serialize(result);
            },
            "get_allocations",
            "Get memory allocation statistics by type. Requires allocation events in the trace."));

        tools.Add(AIFunctionFactory.Create(
            ([Description("Start time in ms (optional, defaults to trace start)")] double? from,
             [Description("End time in ms (optional, defaults to trace end)")] double? to,
             [Description("Number of time buckets (default 50)")] int? buckets,
             [Description("Comma-separated lanes to include: gc,cpu,exceptions,alloc,jit,events (default: gc,cpu,exceptions)")] string? lanes) =>
            {
                var laneSet = new HashSet<string>(
                    (lanes ?? "gc,cpu,exceptions").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                var result = TraceAnalyzer.GetTimeline(traceSession.TraceLog, from, to, buckets ?? 50, laneSet);
                return JsonSerializer.Serialize(result);
            },
            "get_timeline",
            "Get a unified timeline with multiple event lanes bucketed over time. Returns all requested lanes in a single call so you can correlate events temporally. Use this to answer questions like 'what was happening during GC pauses' or 'correlate CPU with exceptions'."));

        tools.Add(AIFunctionFactory.Create(
            ([Description("Center timestamp in ms to inspect")] double at,
             [Description("Half-window size in ms around the center point (default 100ms, so ±100ms)")] double? window) =>
            {
                var result = TraceAnalyzer.GetSnapshot(traceSession.TraceLog, at, window ?? 100);
                return JsonSerializer.Serialize(result);
            },
            "get_snapshot",
            "Get a point-in-time snapshot showing everything that happened around a specific timestamp. Returns GC events, top CPU methods, exceptions, and event counts in one call. Use after spotting something interesting in the timeline to get details."));

        return tools;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            try { await session.DisposeAsync(); } catch { }
        }
        _sessions.Clear();

        if (_client != null)
        {
            try { await _client.StopAsync(); } catch { }
            _client = null;
        }
    }
}

public class ChatResponse
{
    public string Content { get; set; } = "";
}

public record ChatRequest(string Message);
