using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PVAnalyze.Server;

public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

        app.MapPost("/api/traces/open", (OpenTraceRequest request, TraceSessionManager manager) =>
        {
            Console.Error.WriteLine($"[OpenTrace] Received request - FilePath: '{request.FilePath}'");

            if (string.IsNullOrWhiteSpace(request.FilePath))
                return Results.BadRequest(new { error = "filePath is required" });

            if (!File.Exists(request.FilePath))
                return Results.BadRequest(new { error = $"File not found: {request.FilePath}" });

            try
            {
                var id = manager.OpenTrace(request.FilePath);
                var session = manager.GetSession(id)!;
                var info = TraceAnalyzer.GetTraceInfo(session);
                return Results.Ok(new { id, filePath = request.FilePath, info });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Failed to open trace: {ex.Message}" });
            }
        });

        app.MapGet("/api/traces", (TraceSessionManager manager) =>
        {
            return Results.Ok(manager.ListSessions());
        });

        app.MapGet("/api/traces/{id}/info", (string id, TraceSessionManager manager) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });
            return Results.Ok(TraceAnalyzer.GetTraceInfo(session));
        });

        app.MapDelete("/api/traces/{id}", (string id, TraceSessionManager manager) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });
            manager.CloseSession(id);
            return Results.Ok(new { message = "Session closed" });
        });

        app.MapGet("/api/traces/{id}/gcstats", (string id, TraceSessionManager manager,
            bool? timeline, int? longest, double? from, double? to, string? process) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var result = TraceAnalyzer.GetGcStats(session.TraceLog, process,
                    timeline ?? false, longest, from, to);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/traces/{id}/jitstats", (string id, TraceSessionManager manager,
            string? process) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var result = TraceAnalyzer.GetJitStats(session.TraceLog, process);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/traces/{id}/cpustacks", (string id, TraceSessionManager manager,
            int? top, string? groupBy, bool? inclusive, double? from, double? to) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var result = TraceAnalyzer.GetCpuStacks(session.TraceLog,
                    top ?? 20, groupBy ?? "method", inclusive ?? false, from, to);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/traces/{id}/events", (string id, TraceSessionManager manager,
            string? type, string? provider, bool? list, int? limit, double? from, double? to,
            int? pid, int? tid, string? payload) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                if (list == true)
                {
                    var result = TraceAnalyzer.GetEventTypeList(session.TraceLog, provider, from, to);
                    return Results.Ok(result);
                }
                else
                {
                    var result = TraceAnalyzer.GetEvents(session.TraceLog, type, provider,
                        limit ?? 100, from, to, pid, tid, payload);
                    return Results.Ok(result);
                }
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/traces/{id}/exceptions", (string id, TraceSessionManager manager,
            string? type, double? from, double? to, int? limit) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var result = TraceAnalyzer.GetExceptions(session.TraceLog, type, from, to, limit ?? 100);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.Map("/ws", async (HttpContext context, TraceSessionManager manager) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            var ws = await context.WebSockets.AcceptWebSocketAsync();
            await WebSocketHandler.HandleAsync(ws, manager);
        });

        app.MapGet("/api/traces/{id}/allocations", (string id, TraceSessionManager manager,
            int? top, string? groupBy, double? from, double? to) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var result = TraceAnalyzer.GetAllocations(session.TraceLog,
                    top ?? 20, groupBy ?? "type", from, to);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/traces/{id}/calltree", (string id, TraceSessionManager manager,
            int? depth) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var callTree = session.GetOrBuildCallTree();
                var result = TraceAnalyzer.GetCallTree(callTree, depth ?? 2);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/traces/{id}/calltree/hotpath", (string id, TraceSessionManager manager,
            string? path) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var callTree = session.GetOrBuildCallTree();
                var pathArray = string.IsNullOrEmpty(path)
                    ? Array.Empty<int>()
                    : path.Split(',').Select(int.Parse).ToArray();
                var result = TraceAnalyzer.GetHotPath(callTree, pathArray);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HotPath] {ex}");
                return Results.BadRequest(new { error = ex.Message, stack = ex.StackTrace });
            }
        });

        app.MapGet("/api/traces/{id}/calltree/children", (string id, TraceSessionManager manager,
            string? path, int? depth) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var callTree = session.GetOrBuildCallTree();
                var pathArray = string.IsNullOrEmpty(path)
                    ? Array.Empty<int>()
                    : path.Split(',').Select(int.Parse).ToArray();
                var result = TraceAnalyzer.GetCallTreeChildren(callTree, pathArray, depth ?? 1);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/traces/{id}/calltree/callercallee", (string id, TraceSessionManager manager,
            string method) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var callTree = session.GetOrBuildCallTree();
                var result = TraceAnalyzer.GetCallerCallee(callTree, method);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // --- Timeline Correlation ---
        app.MapGet("/api/traces/{id}/timeline", (string id, TraceSessionManager manager,
            double? from, double? to, int? buckets, string? lanes) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var laneSet = new HashSet<string>(
                    (lanes ?? "gc,cpu,exceptions").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
                var result = TraceAnalyzer.GetTimeline(session.TraceLog, from, to, buckets ?? 50, laneSet);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // --- Point-in-Time Snapshot ---
        app.MapGet("/api/traces/{id}/snapshot", (string id, TraceSessionManager manager,
            double at, double? window) =>
        {
            var session = manager.GetSession(id);
            if (session == null) return Results.NotFound(new { error = "Session not found" });

            try
            {
                var result = TraceAnalyzer.GetSnapshot(session.TraceLog, at, window ?? 100);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // --- Copilot Chat ---
        app.MapPost("/api/traces/{id}/chat", async (string id, ChatRequest request, CopilotService copilot) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "message is required" });

            try
            {
                var response = await copilot.ChatAsync(id, request.Message);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Chat] {ex}");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/traces/{id}/chat/stream", async (string id, ChatRequest request, CopilotService copilot, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "message is required" });
                return;
            }

            try
            {
                await copilot.ChatStreamAsync(id, request.Message, context.Response, context.RequestAborted);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ChatStream] {ex}");
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            }
        });
    }
}
