using System.CommandLine;
using System.Text.Json;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using PVAnalyze;

namespace PVAnalyze.Commands;

public static class TimelineCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file", "Path to the .nettrace file to analyze");
        var lanesOption = new Option<string>("--lanes", () => "gc,cpu,exceptions", "Comma-separated lanes: gc,cpu,exceptions,alloc,jit,events");
        var bucketsOption = new Option<int>("--buckets", () => 50, "Number of time buckets");
        var fromOption = new Option<double?>("--from", "Start time in milliseconds");
        var toOption = new Option<double?>("--to", "End time in milliseconds");
        var formatOption = new Option<OutputFormat>("--format", () => OutputFormat.Json, "Output format");

        var command = new Command("timeline", "Show a unified timeline with multiple event lanes bucketed over time")
        {
            traceFileArg, lanesOption, bucketsOption, fromOption, toOption, formatOption
        };

        command.SetHandler(Execute, traceFileArg, lanesOption, bucketsOption, fromOption, toOption, formatOption);
        return command;
    }

    private static void Execute(FileInfo traceFile, string lanes, int buckets,
        double? fromMs, double? toMs, OutputFormat format)
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

            var laneSet = new HashSet<string>(
                lanes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var result = TraceAnalyzer.GetTimeline(traceLog, fromMs, toMs, buckets, laneSet);

            if (format == OutputFormat.Json)
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                Console.WriteLine(JsonSerializer.Serialize(result, options));
            }
            else
            {
                Console.WriteLine($"=== Timeline: {result.From:F0}ms - {result.To:F0}ms ({result.BucketCount} buckets, {result.BucketSizeMs:F1}ms each) ===");
                Console.WriteLine();
                foreach (var (laneName, bucketArray) in result.Lanes)
                {
                    Console.WriteLine($"  Lane: {laneName} ({bucketArray.Length} buckets)");
                }
            }

            try { File.Delete(etlxPath); } catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
        }
    }
}
