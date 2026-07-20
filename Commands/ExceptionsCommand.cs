using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ExceptionsResponse))]
[JsonSerializable(typeof(Dictionary<string, int>))]
internal partial class ExceptionsJsonContext : JsonSerializerContext { }

public static class ExceptionsCommand
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
            Description = "Filter by exception type (substring match)"
        };
        var fromOption = new Option<double?>("--from")
        {
            Description = "Start time in milliseconds"
        };
        var toOption = new Option<double?>("--to")
        {
            Description = "End time in milliseconds"
        };
        var limitOption = new Option<int>("--limit")
        {
            DefaultValueFactory = _ => 100,
            Description = "Maximum number of exceptions to show"
        };

        var command = new Command("exceptions", "List exceptions thrown during the trace")
        {
            traceFileArg,
            formatOption,
            typeOption,
            fromOption,
            toOption,
            limitOption
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var traceFile = parseResult.GetValue(traceFileArg)!;
            var format = parseResult.GetValue(formatOption)!;
            var typeFilter = parseResult.GetValue(typeOption)!;
            var fromMs = parseResult.GetValue(fromOption)!;
            var toMs = parseResult.GetValue(toOption)!;
            var limit = parseResult.GetValue(limitOption)!;
            await Execute(traceFile, format, typeFilter, fromMs, toMs, limit, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(FileInfo traceFile, OutputFormat format, string? typeFilter,
        double? fromMs, double? toMs, int limit, CancellationToken cancellationToken)
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

            var exceptions = new List<ExceptionEntry>();
            var exceptionCounts = new Dictionary<string, int>();

            // Look for exception events
            foreach (var evt in traceLog.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Time filtering
                if (fromMs.HasValue && evt.TimeStampRelativeMSec < fromMs.Value) continue;
                if (toMs.HasValue && evt.TimeStampRelativeMSec > toMs.Value) continue;

                // Check if this is an exception event — only actual throws, not EH flow
                if (evt.EventName == "Exception/Start" ||
                    evt.EventName == "ExceptionThrown_V1" ||
                    evt.EventName == "FirstChanceException")
                {
                    var exType = GetExceptionType(evt);
                    var exMessage = GetExceptionMessage(evt);

                    // Type filtering
                    if (typeFilter != null && 
                        !exType.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Track counts
                    if (!exceptionCounts.ContainsKey(exType))
                        exceptionCounts[exType] = 0;
                    exceptionCounts[exType]++;

                    if (exceptions.Count < limit)
                    {
                        exceptions.Add(new ExceptionEntry(
                            Math.Round(evt.TimeStampRelativeMSec, 3),
                            exType,
                            exMessage,
                            evt.ProcessID,
                            evt.ThreadID));
                    }
                }
            }

            if (exceptions.Count == 0)
            {
                Console.Error.WriteLine("No exceptions found in trace.");
                Console.Error.WriteLine("Ensure the trace was collected with exception events enabled.");
                return;
            }

            if (format == OutputFormat.Json)
            {
                var result = new ExceptionsResponse(
                    exceptions,
                    exceptionCounts.ToDictionary(k => k.Key, k => k.Value));
                await JsonOutput.WriteAsync(result, ExceptionsJsonContext.Default.ExceptionsResponse, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine("=== Exception Summary ===");
                Console.WriteLine();
                foreach (var kvp in exceptionCounts.OrderByDescending(k => k.Value))
                {
                    Console.WriteLine($"  {kvp.Value,5}x  {kvp.Key}");
                }
                Console.WriteLine();
                Console.WriteLine($"=== Exceptions ({Math.Min(exceptions.Count, limit)} of {exceptionCounts.Values.Sum()}) ===");
                Console.WriteLine();
                Console.WriteLine($"{"Time (ms)",12}  {"PID",6}  {"TID",6}  {"Type",-40}  Message");
                Console.WriteLine(new string('-', 100));
                
                foreach (var ex in exceptions)
                {
                    Console.WriteLine($"{ex.TimestampMs,12:F3}  {ex.ProcessId,6}  {ex.ThreadId,6}  {Truncate(ex.Type, 40),-40}  {Truncate(ex.Message, 40)}");
                }
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

    private static string GetExceptionType(TraceEvent evt)
    {
        var payloadNames = evt.PayloadNames;
        // First: look for ExceptionType specifically
        for (int i = 0; i < payloadNames.Length; i++)
        {
            if (string.Equals(payloadNames[i], "ExceptionType", StringComparison.OrdinalIgnoreCase))
            {
                var value = evt.PayloadValue(i)?.ToString();
                if (!string.IsNullOrEmpty(value)) return value!;
            }
        }
        // Fallback: any type/name payload
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
            var name = payloadNames[i].ToLower();
            if (name.Contains("message"))
            {
                return evt.PayloadValue(i)?.ToString() ?? "";
            }
        }
        return "";
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= maxLen ? s : s.Substring(0, maxLen - 3) + "...";
    }
}
