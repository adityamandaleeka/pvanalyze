using System.CommandLine;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

public static class EventsCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file", "Path to the .nettrace file to analyze");
        var formatOption = new Option<OutputFormat>("--format", () => OutputFormat.Text, "Output format");
        var typeOption = new Option<string?>("--type", "Filter by event type name (substring match)");
        var providerOption = new Option<string?>("--provider", "Filter by provider name (substring match)");
        var listOption = new Option<bool>("--list", "List unique event types only");
        var limitOption = new Option<int>("--limit", () => 100, "Maximum number of events to show");
        var fromOption = new Option<double?>("--from", "Start time in milliseconds");
        var toOption = new Option<double?>("--to", "End time in milliseconds");
        var pidOption = new Option<int?>("--pid", "Filter by process ID");
        var tidOption = new Option<int?>("--tid", "Filter by thread ID");
        var payloadOption = new Option<string?>("--payload", "Filter by payload content (substring match)");

        var command = new Command("events", "List and filter events from a trace")
        {
            traceFileArg,
            formatOption,
            typeOption,
            providerOption,
            listOption,
            limitOption,
            fromOption,
            toOption,
            pidOption,
            tidOption,
            payloadOption
        };

        command.SetHandler(async (context) =>
        {
            var traceFile = context.ParseResult.GetValueForArgument(traceFileArg);
            var format = context.ParseResult.GetValueForOption(formatOption);
            var typeFilter = context.ParseResult.GetValueForOption(typeOption);
            var providerFilter = context.ParseResult.GetValueForOption(providerOption);
            var listOnly = context.ParseResult.GetValueForOption(listOption);
            var limit = context.ParseResult.GetValueForOption(limitOption);
            var fromMs = context.ParseResult.GetValueForOption(fromOption);
            var toMs = context.ParseResult.GetValueForOption(toOption);
            var pid = context.ParseResult.GetValueForOption(pidOption);
            var tid = context.ParseResult.GetValueForOption(tidOption);
            var payload = context.ParseResult.GetValueForOption(payloadOption);
            Execute(traceFile, format, typeFilter, providerFilter, listOnly, limit, fromMs, toMs, pid, tid, payload);
        });
        return command;
    }

    private static void Execute(FileInfo traceFile, OutputFormat format, string? typeFilter, 
        string? providerFilter, bool listOnly, int limit, double? fromMs, double? toMs,
        int? pidFilter, int? tidFilter, string? payloadFilter)
    {
        if (!traceFile.Exists)
        {
            Console.Error.WriteLine($"Error: File not found: {traceFile.FullName}");
            return;
        }

        try
        {
            string etlxPath = Etlx.TraceLog.CreateFromEventPipeDataFile(traceFile.FullName);
            
            using var traceLog = new Etlx.TraceLog(etlxPath);

            if (listOnly)
            {
                ListEventTypes(traceLog, format, providerFilter, fromMs, toMs);
            }
            else
            {
                ListEvents(traceLog, format, typeFilter, providerFilter, limit, fromMs, toMs,
                    pidFilter, tidFilter, payloadFilter);
            }

            // Clean up
            try { File.Delete(etlxPath); } catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
        }
    }

    private static void ListEventTypes(Etlx.TraceLog traceLog, OutputFormat format, 
        string? providerFilter, double? fromMs, double? toMs)
    {
        var eventTypes = new Dictionary<string, EventTypeInfo>();

        foreach (var evt in traceLog.Events)
        {
            // Time filtering
            if (fromMs.HasValue && evt.TimeStampRelativeMSec < fromMs.Value) continue;
            if (toMs.HasValue && evt.TimeStampRelativeMSec > toMs.Value) continue;

            // Provider filtering
            if (providerFilter != null && 
                !evt.ProviderName.Contains(providerFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = $"{evt.ProviderName}/{evt.EventName}";
            if (!eventTypes.TryGetValue(key, out var info))
            {
                info = new EventTypeInfo 
                { 
                    Provider = evt.ProviderName, 
                    EventName = evt.EventName,
                    Count = 0 
                };
                eventTypes[key] = info;
            }
            info.Count++;
        }

        var sorted = eventTypes.Values.OrderByDescending(e => e.Count).ToList();

        if (format == OutputFormat.Json)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            Console.WriteLine(JsonSerializer.Serialize(sorted, options));
        }
        else
        {
            Console.WriteLine($"{"Count",10}  {"Provider",-40}  Event");
            Console.WriteLine(new string('-', 80));
            foreach (var info in sorted)
            {
                Console.WriteLine($"{info.Count,10}  {Truncate(info.Provider, 40),-40}  {info.EventName}");
            }
            Console.WriteLine();
            Console.WriteLine($"Total: {sorted.Count} unique event types");
        }
    }

    private static void ListEvents(Etlx.TraceLog traceLog, OutputFormat format, 
        string? typeFilter, string? providerFilter, int limit, double? fromMs, double? toMs,
        int? pidFilter, int? tidFilter, string? payloadFilter)
    {
        var events = new List<EventInfo>();
        int count = 0;

        foreach (var evt in traceLog.Events)
        {
            // Time filtering
            if (fromMs.HasValue && evt.TimeStampRelativeMSec < fromMs.Value) continue;
            if (toMs.HasValue && evt.TimeStampRelativeMSec > toMs.Value) continue;

            // Type filtering
            if (typeFilter != null && 
                !evt.EventName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            // Provider filtering
            if (providerFilter != null && 
                !evt.ProviderName.Contains(providerFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            // PID/TID filtering
            if (pidFilter.HasValue && evt.ProcessID != pidFilter.Value) continue;
            if (tidFilter.HasValue && evt.ThreadID != tidFilter.Value) continue;

            var message = GetEventMessage(evt);

            // Payload filtering
            if (payloadFilter != null &&
                !message.Contains(payloadFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            events.Add(new EventInfo
            {
                TimestampMs = Math.Round(evt.TimeStampRelativeMSec, 3),
                Provider = evt.ProviderName,
                EventName = evt.EventName,
                ProcessId = evt.ProcessID,
                ThreadId = evt.ThreadID,
                Message = message
            });

            count++;
            if (count >= limit) break;
        }

        if (format == OutputFormat.Json)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            Console.WriteLine(JsonSerializer.Serialize(events, options));
        }
        else
        {
            Console.WriteLine($"{"Time (ms)",12}  {"PID",6}  {"TID",6}  {"Event",-30}  Message");
            Console.WriteLine(new string('-', 100));
            foreach (var evt in events)
            {
                Console.WriteLine($"{evt.TimestampMs,12:F3}  {evt.ProcessId,6}  {evt.ThreadId,6}  {Truncate(evt.EventName, 30),-30}  {Truncate(evt.Message, 40)}");
            }
            Console.WriteLine();
            Console.WriteLine($"Showing {events.Count} events (limit: {limit})");
        }
    }

    private static string GetEventMessage(TraceEvent evt)
    {
        try
        {
            // Try to get a meaningful summary from the event
            var payloadNames = evt.PayloadNames;
            if (payloadNames.Length == 0) return "";

            var parts = new List<string>();
            for (int i = 0; i < Math.Min(payloadNames.Length, 3); i++)
            {
                var name = payloadNames[i];
                var value = evt.PayloadValue(i);
                if (value != null)
                {
                    var valueStr = value.ToString();
                    if (!string.IsNullOrEmpty(valueStr) && valueStr.Length <= 50)
                    {
                        parts.Add($"{name}={valueStr}");
                    }
                }
            }
            return string.Join(", ", parts);
        }
        catch
        {
            return "";
        }
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= maxLen ? s : s.Substring(0, maxLen - 3) + "...";
    }

    private class EventTypeInfo
    {
        public string Provider { get; set; } = "";
        public string EventName { get; set; } = "";
        public int Count { get; set; }
    }

    private class EventInfo
    {
        public double TimestampMs { get; set; }
        public string Provider { get; set; } = "";
        public string EventName { get; set; } = "";
        public int ProcessId { get; set; }
        public int ThreadId { get; set; }
        public string Message { get; set; } = "";
    }
}
