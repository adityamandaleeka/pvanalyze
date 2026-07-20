using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

internal record PmcCounterStats(int ProfileSource, long Samples);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<PmcCounterStats>))]
internal partial class PmcStatsJsonContext : JsonSerializerContext { }

public static class PmcStatsCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file")
        {
            Description = "Path to a trace file containing PMC samples"
        };
        var formatOption = new Option<OutputFormat>("--format")
        {
            DefaultValueFactory = _ => OutputFormat.Text,
            Description = "Output format"
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

        var command = new Command("pmcstats", "Aggregate hardware-counter samples by profile source")
        {
            traceFileArg,
            formatOption,
            fromOption,
            toOption,
            pidOption
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var traceFile = parseResult.GetValue(traceFileArg)!;
            var format = parseResult.GetValue(formatOption)!;
            var fromMs = parseResult.GetValue(fromOption)!;
            var toMs = parseResult.GetValue(toOption)!;
            var pid = parseResult.GetValue(pidOption)!;
            await Execute(traceFile, format, fromMs, toMs, pid, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task Execute(
        FileInfo traceFile,
        OutputFormat format,
        double? fromMs,
        double? toMs,
        int? processId,
        CancellationToken cancellationToken)
    {
        if (!traceFile.Exists)
        {
            Console.Error.WriteLine($"Error: File not found: {traceFile.FullName}");
            return;
        }

        try
        {
            var etlxPath = string.Equals(traceFile.Extension, ".etlx", StringComparison.OrdinalIgnoreCase)
                ? traceFile.FullName
                : await EtlxCache.GetOrCreateEtlxAsync(traceFile.FullName, cancellationToken).ConfigureAwait(false);
            using var traceLog = new Etlx.TraceLog(etlxPath);
            var counters = new Dictionary<int, long>();

            foreach (var evt in traceLog.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evt is not PMCCounterProfTraceData sample
                    || (fromMs.HasValue && evt.TimeStampRelativeMSec < fromMs.Value)
                    || (toMs.HasValue && evt.TimeStampRelativeMSec > toMs.Value)
                    || (processId.HasValue && evt.ProcessID != processId.Value))
                {
                    continue;
                }

                counters.TryGetValue(sample.ProfileSource, out var count);
                counters[sample.ProfileSource] = count + 1;
            }

            var result = counters
                .OrderBy(static entry => entry.Key)
                .Select(static entry => new PmcCounterStats(entry.Key, entry.Value))
                .ToList();

            if (format == OutputFormat.Json)
            {
                await JsonOutput.WriteAsync(
                    result,
                    PmcStatsJsonContext.Default.ListPmcCounterStats,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            Console.WriteLine($"{"Source ID",10}  {"Samples",14}");
            Console.WriteLine(new string('-', 28));
            foreach (var counter in result)
            {
                Console.WriteLine($"{counter.ProfileSource,10}  {counter.Samples,14:N0}");
            }

            Console.WriteLine();
            Console.WriteLine($"Total: {result.Sum(static counter => counter.Samples):N0} PMC samples");
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
