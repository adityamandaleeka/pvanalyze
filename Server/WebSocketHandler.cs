using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PVAnalyze.Server;

public static class WebSocketHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task HandleAsync(WebSocket webSocket, TraceSessionManager manager)
    {
        var buffer = new byte[4096];
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            await ProcessMessage(webSocket, message, manager);
        }
    }

    private static async Task ProcessMessage(WebSocket webSocket, string message, TraceSessionManager manager)
    {
        try
        {
            var doc = JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "subscribe":
                    await HandleSubscribe(webSocket, doc.RootElement, manager);
                    break;
                case "query":
                    await HandleQuery(webSocket, doc.RootElement, manager);
                    break;
                default:
                    await SendMessage(webSocket, new { type = "error", message = $"Unknown message type: {type}" });
                    break;
            }
        }
        catch (Exception ex)
        {
            await SendMessage(webSocket, new { type = "error", message = ex.Message });
        }
    }

    private static async Task HandleSubscribe(WebSocket webSocket, JsonElement root, TraceSessionManager manager)
    {
        var traceId = root.GetProperty("traceId").GetString()!;
        var channel = root.GetProperty("channel").GetString()!;

        var session = manager.GetSession(traceId);
        if (session == null)
        {
            await SendMessage(webSocket, new { type = "error", message = "Session not found" });
            return;
        }

        // Parse optional filters
        double? fromMs = null, toMs = null;
        string? providerFilter = null, typeFilter = null;
        if (root.TryGetProperty("filter", out var filter))
        {
            if (filter.TryGetProperty("from", out var f)) fromMs = f.GetDouble();
            if (filter.TryGetProperty("to", out var t)) toMs = t.GetDouble();
            if (filter.TryGetProperty("provider", out var p)) providerFilter = p.GetString();
            if (filter.TryGetProperty("type", out var tp)) typeFilter = tp.GetString();
        }

        await SendMessage(webSocket, new { type = "subscribed", channel, traceId });

        // Stream events based on channel
        switch (channel)
        {
            case "events":
                await StreamEvents(webSocket, session, providerFilter, typeFilter, fromMs, toMs);
                break;
            case "gc":
                await StreamGcEvents(webSocket, session, fromMs, toMs);
                break;
            default:
                await SendMessage(webSocket, new { type = "error", message = $"Unknown channel: {channel}" });
                break;
        }

        await SendMessage(webSocket, new { type = "stream_end", channel });
    }

    private static async Task HandleQuery(WebSocket webSocket, JsonElement root, TraceSessionManager manager)
    {
        var traceId = root.GetProperty("traceId").GetString()!;
        var session = manager.GetSession(traceId);
        if (session == null)
        {
            await SendMessage(webSocket, new { type = "error", message = "Session not found" });
            return;
        }

        var result = DataQueryEngine.Execute(session, root);
        await SendMessage(webSocket, new { type = "query_result", data = result });
    }

    private static async Task StreamEvents(WebSocket webSocket, TraceSession session,
        string? providerFilter, string? typeFilter, double? fromMs, double? toMs)
    {
        int count = 0;
        foreach (var evt in session.TraceLog.Events)
        {
            if (webSocket.State != WebSocketState.Open) break;
            if (fromMs.HasValue && evt.TimeStampRelativeMSec < fromMs.Value) continue;
            if (toMs.HasValue && evt.TimeStampRelativeMSec > toMs.Value) continue;
            if (providerFilter != null && !evt.ProviderName.Contains(providerFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (typeFilter != null && !evt.EventName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;

            await SendMessage(webSocket, new
            {
                type = "event",
                data = new
                {
                    timestampMs = Math.Round(evt.TimeStampRelativeMSec, 3),
                    provider = evt.ProviderName,
                    eventName = evt.EventName,
                    processId = evt.ProcessID,
                    threadId = evt.ThreadID
                }
            });

            count++;
            if (count % 1000 == 0)
            {
                await SendMessage(webSocket, new { type = "progress", count, message = $"Streamed {count} events..." });
            }
        }
    }

    private static async Task StreamGcEvents(WebSocket webSocket, TraceSession session,
        double? fromMs, double? toMs)
    {
        var result = TraceAnalyzer.GetGcStats(session.TraceLog, null, true, null, fromMs, toMs);
        if (result.Timeline != null)
        {
            foreach (var gc in result.Timeline)
            {
                if (webSocket.State != WebSocketState.Open) break;
                await SendMessage(webSocket, new { type = "event", channel = "gc", data = gc });
            }
        }
    }

    private static async Task SendMessage(WebSocket webSocket, object message)
    {
        if (webSocket.State != WebSocketState.Open) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await webSocket.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
