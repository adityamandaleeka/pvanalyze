using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using PVAnalyze;

namespace PVAnalyze.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SnapshotResponse))]
internal partial class SnapshotJsonContext : JsonSerializerContext { }

public static class SnapshotCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file")
        {
            Description = "Path to a .nettrace, .etl, .etl.zip, or .etlx file"
        };
        var atOption = new Option<double>("--at")
        {
            Description = "Center timestamp in milliseconds",
            Required = true
        };
        var windowOption = new Option<double>("--window")
        {
            DefaultValueFactory = _ => 100,
            Description = "Half-window size in ms (default ±100ms)"
        };
        var formatOption = new Option<OutputFormat>("--format")
        {
            DefaultValueFactory = _ => OutputFormat.Json,
            Description = "Output format"
        };

        var command = new Command("snapshot", "Show what was happening at a specific point in time")
        {
            traceFileArg, atOption, windowOption, formatOption
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var traceFile = parseResult.GetValue(traceFileArg)!;
            var at = parseResult.GetValue(atOption)!;
            var window = parseResult.GetValue(windowOption)!;
            var format = parseResult.GetValue(formatOption)!;
            await Execute(traceFile, at, window, format, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(FileInfo traceFile, double at, double window, OutputFormat format, CancellationToken cancellationToken)
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

            var result = TraceAnalyzer.GetSnapshot(traceLog, at, window);

            if (format == OutputFormat.Json)
            {
                await JsonOutput.WriteAsync(result, SnapshotJsonContext.Default.SnapshotResponse, cancellationToken).ConfigureAwait(false);
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
}
