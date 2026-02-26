using System.CommandLine;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

public static class ExceptionsCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file", "Path to the .nettrace file to analyze");
        var formatOption = new Option<OutputFormat>("--format", () => OutputFormat.Text, "Output format");
        var typeOption = new Option<string?>("--type", "Filter by exception type (substring match)");
        var fromOption = new Option<double?>("--from", "Start time in milliseconds");
        var toOption = new Option<double?>("--to", "End time in milliseconds");
        var limitOption = new Option<int>("--limit", () => 100, "Maximum number of exceptions to show");

        var command = new Command("exceptions", "List exceptions thrown during the trace")
        {
            traceFileArg,
            formatOption,
            typeOption,
            fromOption,
            toOption,
            limitOption
        };

        command.SetHandler(Execute, traceFileArg, formatOption, typeOption, fromOption, toOption, limitOption);
        return command;
    }

    private static void Execute(FileInfo traceFile, OutputFormat format, string? typeFilter,
        double? fromMs, double? toMs, int limit)
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

            var exceptions = new List<ExceptionInfo>();
            var exceptionCounts = new Dictionary<string, int>();

            // Look for exception events
            foreach (var evt in traceLog.Events)
            {
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
                        exceptions.Add(new ExceptionInfo
                        {
                            TimestampMs = Math.Round(evt.TimeStampRelativeMSec, 3),
                            Type = exType,
                            Message = exMessage,
                            ProcessId = evt.ProcessID,
                            ThreadId = evt.ThreadID
                        });
                    }
                }
            }

            if (exceptions.Count == 0)
            {
                Console.Error.WriteLine("No exceptions found in trace.");
                Console.Error.WriteLine("Ensure the trace was collected with exception events enabled.");
                
                // Clean up
                try { File.Delete(etlxPath); } catch { }
                return;
            }

            if (format == OutputFormat.Json)
            {
                var result = new
                {
                    summary = exceptionCounts.OrderByDescending(kvp => kvp.Value)
                        .Select(kvp => new { type = kvp.Key, count = kvp.Value }),
                    exceptions = exceptions
                };
                var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                Console.WriteLine(JsonSerializer.Serialize(result, options));
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

            // Clean up
            try { File.Delete(etlxPath); } catch { }
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

    private class ExceptionInfo
    {
        public double TimestampMs { get; set; }
        public string Type { get; set; } = "";
        public string Message { get; set; } = "";
        public int ProcessId { get; set; }
        public int ThreadId { get; set; }
    }
}
