using System.CommandLine;
using System.Text.Json;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using PVAnalyze.Server;

namespace PVAnalyze.Commands;

public static class SnapshotCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file", "Path to the .nettrace file to analyze");
        var atOption = new Option<double>("--at", "Center timestamp in milliseconds") { IsRequired = true };
        var windowOption = new Option<double>("--window", () => 100, "Half-window size in ms (default ±100ms)");
        var formatOption = new Option<OutputFormat>("--format", () => OutputFormat.Json, "Output format");

        var command = new Command("snapshot", "Show what was happening at a specific point in time")
        {
            traceFileArg, atOption, windowOption, formatOption
        };

        command.SetHandler(Execute, traceFileArg, atOption, windowOption, formatOption);
        return command;
    }

    private static void Execute(FileInfo traceFile, double at, double window, OutputFormat format)
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

            var result = TraceAnalyzer.GetSnapshot(traceLog, at, window);

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
                Console.WriteLine($"=== Snapshot at {result.At:F1}ms (window: {result.WindowFrom:F1} - {result.WindowTo:F1}ms) ===");
                Console.WriteLine();

                if (result.Gc != null)
                {
                    Console.WriteLine($"  GC: {result.Gc.Count} event(s)");
                    foreach (var gc in result.Gc.GcEvents)
                        Console.WriteLine($"    Gen{gc.Generation} at {gc.StartTimeMs:F1}ms, pause {gc.PauseDurationMs:F2}ms");
                }

                if (result.Cpu != null)
                {
                    Console.WriteLine($"  CPU: {result.Cpu.SampleCount} samples");
                    foreach (var m in result.Cpu.TopMethods)
                        Console.WriteLine($"    {m.Percent:F1}% {m.Name}");
                }

                if (result.Exceptions != null)
                    Console.WriteLine($"  Exceptions: {result.Exceptions.Count}");

                if (result.Events != null)
                    Console.WriteLine($"  Events: {result.Events.TotalCount} total");
            }

            try { File.Delete(etlxPath); } catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
        }
    }
}
