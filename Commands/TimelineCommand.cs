using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using PVAnalyze;

namespace PVAnalyze.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TimelineResponse))]
[JsonSerializable(typeof(GcBucket))]
[JsonSerializable(typeof(CpuBucket))]
[JsonSerializable(typeof(ExceptionBucket))]
[JsonSerializable(typeof(AllocBucket))]
[JsonSerializable(typeof(JitBucket))]
[JsonSerializable(typeof(EventBucket))]
internal partial class TimelineJsonContext : JsonSerializerContext { }

public static class TimelineCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file")
        {
            Description = "Path to a .nettrace, .etl, .etl.zip, or .etlx file"
        };
        var lanesOption = new Option<string>("--lanes")
        {
            DefaultValueFactory = _ => "gc,cpu,exceptions",
            Description = "Comma-separated lanes: gc,cpu,exceptions,alloc,jit,events"
        };
        var bucketsOption = new Option<int>("--buckets")
        {
            DefaultValueFactory = _ => 50,
            Description = "Number of time buckets"
        };
        var fromOption = new Option<double?>("--from")
        {
            Description = "Start time in milliseconds"
        };
        var toOption = new Option<double?>("--to")
        {
            Description = "End time in milliseconds"
        };
        var formatOption = new Option<OutputFormat>("--format")
        {
            DefaultValueFactory = _ => OutputFormat.Json,
            Description = "Output format"
        };

        var command = new Command("timeline", "Show a unified timeline with multiple event lanes bucketed over time")
        {
            traceFileArg, lanesOption, bucketsOption, fromOption, toOption, formatOption
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var traceFile = parseResult.GetValue(traceFileArg)!;
            var lanes = parseResult.GetValue(lanesOption)!;
            var buckets = parseResult.GetValue(bucketsOption)!;
            var fromMs = parseResult.GetValue(fromOption)!;
            var toMs = parseResult.GetValue(toOption)!;
            var format = parseResult.GetValue(formatOption)!;
            await Execute(traceFile, lanes, buckets, fromMs, toMs, format, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(FileInfo traceFile, string lanes, int buckets,
        double? fromMs, double? toMs, OutputFormat format, CancellationToken cancellationToken)
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

            var laneSet = new HashSet<string>(
                lanes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var result = TraceAnalyzer.GetTimeline(traceLog, fromMs, toMs, buckets, laneSet);

            if (format == OutputFormat.Json)
            {
                await JsonOutput.WriteAsync(result, TimelineJsonContext.Default.TimelineResponse, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine($"=== Timeline: {result.From:F0}ms - {result.To:F0}ms ({result.BucketCount} buckets, {result.BucketSizeMs:F1}ms each) ===");
                Console.WriteLine();
                foreach (var (laneName, bucketArray) in result.Lanes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Console.WriteLine($"  Lane: {laneName} ({bucketArray.Length} buckets)");
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
}
