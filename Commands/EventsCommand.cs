using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

internal class EventTypeInfo
{
    public string Provider { get; set; } = "";
    public string EventName { get; set; } = "";
    public int Count { get; set; }
}

internal class EventInfo
{
    public double TimestampMs { get; set; }
    public string Provider { get; set; } = "";
    public string EventName { get; set; } = "";
    public int ProcessId { get; set; }
    public int ThreadId { get; set; }
    public string Message { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<EventTypeInfo>))]
[JsonSerializable(typeof(List<EventInfo>))]
internal partial class EventsJsonContext : JsonSerializerContext { }

public static class EventsCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file")
        {
            Description = "Path to a .nettrace, .etl, .etl.zip, or .etlx file"
        };
        var formatOption = new Option<OutputFormat>("--format")
        {
            DefaultValueFactory = _ => OutputFormat.Text,
            Description = "Output format"
        };
        var typeOption = new Option<string?>("--type")
        {
            Description = "Filter by event type name (substring match)"
        };
        var providerOption = new Option<string?>("--provider")
        {
            Description = "Filter by provider name (substring match)"
        };
        var listOption = new Option<bool>("--list")
        {
            Description = "List unique event types only"
        };
        var limitOption = new Option<int>("--limit")
        {
            DefaultValueFactory = _ => 100,
            Description = "Maximum number of events to show"
        };
        var fromOption = new Option<double?>("--from")
        {
            Description = "Start time in milliseconds"
        };
        var toOption = new Option<double?>("--to")
        {
            Description = "End time in milliseconds"
        };
        var pidOption = new Option<int?>("--pid")
        {
            Description = "Filter by process ID"
        };
        var tidOption = new Option<int?>("--tid")
        {
            Description = "Filter by thread ID"
        };
        var payloadOption = new Option<string?>("--payload")
        {
            Description = "Filter by payload content (substring match)"
        };

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

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var traceFile = parseResult.GetValue(traceFileArg)!;
            var format = parseResult.GetValue(formatOption)!;
            var typeFilter = parseResult.GetValue(typeOption)!;
            var providerFilter = parseResult.GetValue(providerOption)!;
            var listOnly = parseResult.GetValue(listOption)!;
            var limit = parseResult.GetValue(limitOption)!;
            var fromMs = parseResult.GetValue(fromOption)!;
            var toMs = parseResult.GetValue(toOption)!;
            var pid = parseResult.GetValue(pidOption)!;
            var tid = parseResult.GetValue(tidOption)!;
            var payload = parseResult.GetValue(payloadOption)!;
            await Execute(traceFile, format, typeFilter, providerFilter, listOnly, limit, fromMs, toMs, pid, tid, payload, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(FileInfo traceFile, OutputFormat format, string? typeFilter, 
        string? providerFilter, bool listOnly, int limit, double? fromMs, double? toMs,
        int? pidFilter, int? tidFilter, string? payloadFilter, CancellationToken cancellationToken)
    {
        if (!traceFile.Exists)
        {
            Console.Error.WriteLine($"Error: File not found: {traceFile.FullName}");
            return;
        }

        try
        {
            string etlxPath = await EtlxCache.GetOrCreateEtlxAsync(traceFile.FullName, cancellationToken).ConfigureAwait(false);
            
            using var traceLog = new Etlx.TraceLog(etlxPath);

            if (listOnly)
            {
                await ListEventTypes(traceLog, format, providerFilter, fromMs, toMs, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ListEvents(traceLog, format, typeFilter, providerFilter, limit, fromMs, toMs,
                    pidFilter, tidFilter, payloadFilter, cancellationToken).ConfigureAwait(false);
            }

        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
        }
    }

    private static async Task ListEventTypes(Etlx.TraceLog traceLog, OutputFormat format, 
        string? providerFilter, double? fromMs, double? toMs, CancellationToken cancellationToken)
    {
        var eventTypes = new Dictionary<string, EventTypeInfo>();

        foreach (var evt in traceLog.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
            await JsonOutput.WriteAsync(sorted, EventsJsonContext.Default.ListEventTypeInfo, cancellationToken).ConfigureAwait(false);
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

    private static async Task ListEvents(Etlx.TraceLog traceLog, OutputFormat format, 
        string? typeFilter, string? providerFilter, int limit, double? fromMs, double? toMs,
        int? pidFilter, int? tidFilter, string? payloadFilter, CancellationToken cancellationToken)
    {
        var events = new List<EventInfo>();
        int count = 0;

        foreach (var evt in traceLog.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
            await JsonOutput.WriteAsync(events, EventsJsonContext.Default.ListEventInfo, cancellationToken).ConfigureAwait(false);
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
}
