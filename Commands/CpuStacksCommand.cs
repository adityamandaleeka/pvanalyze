using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Tracing.Stacks.Formats;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CpuStacksResponse))]
internal partial class CpuStacksJsonContext : JsonSerializerContext { }

public enum GroupBy
{
    Method,
    Module,
    Namespace
}

public static class CpuStacksCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file")
        {
            Description = "Path to a .nettrace, .etl, .etl.zip, or .etlx file"
        };
        var formatOption = new Option<StackOutputFormat>("--format")
        {
            DefaultValueFactory = _ => StackOutputFormat.Text,
            Description = "Output format"
        };
        var topOption = new Option<int>("--top")
        {
            DefaultValueFactory = _ => 20,
            Description = "Number of top items to show"
        };
        var outputOption = new Option<FileInfo?>("--output")
        {
            Description = "Output file (default: stdout)"
        };
        var groupByOption = new Option<GroupBy>("--group-by")
        {
            DefaultValueFactory = _ => GroupBy.Method,
            Description = "Group results by: method, module, or namespace"
        };
        var fromOption = new Option<double?>("--from")
        {
            Description = "Start time in milliseconds"
        };
        var toOption = new Option<double?>("--to")
        {
            Description = "End time in milliseconds"
        };
        var inclusiveOption = new Option<bool>("--inclusive")
        {
            Description = "Sort by inclusive time instead of exclusive"
        };
        var stackSourceOption = new Option<string>("--stack-source")
        {
            DefaultValueFactory = _ => "cpu",
            Description = "Stack source: cpu, threadtime, activity, activity-cpu, or activity-threadtime"
        };
        stackSourceOption.AcceptOnlyFromAmong(
            "cpu", "threadtime", "activity", "activity-cpu", "activity-threadtime");

        var command = new Command("cpustacks", "Analyze CPU, thread-time, or async activity stacks")
        {
            traceFileArg,
            formatOption,
            topOption,
            outputOption,
            groupByOption,
            fromOption,
            toOption,
            inclusiveOption,
            stackSourceOption
        };
        command.Aliases.Add("stacks");

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var traceFile = parseResult.GetValue(traceFileArg)!;
            var format = parseResult.GetValue(formatOption)!;
            var top = parseResult.GetValue(topOption)!;
            var outputFile = parseResult.GetValue(outputOption)!;
            var groupBy = parseResult.GetValue(groupByOption)!;
            var fromMs = parseResult.GetValue(fromOption)!;
            var toMs = parseResult.GetValue(toOption)!;
            var inclusive = parseResult.GetValue(inclusiveOption)!;
            var stackSource = StackSourceFactory.ParseKind(parseResult.GetValue(stackSourceOption)!);
            await Execute(traceFile, format, top, outputFile, groupBy, fromMs, toMs, inclusive, stackSource, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(FileInfo traceFile, StackOutputFormat format, int top, FileInfo? outputFile,
        GroupBy groupBy, double? fromMs, double? toMs, bool sortByInclusive,
        StackSourceKind stackSourceKind, CancellationToken cancellationToken)
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
            
            var stackSource = StackSourceFactory.Create(
                traceLog, stackSourceKind, fromMs, toMs, out stackSourceKind);

            switch (format)
            {
                case StackOutputFormat.Speedscope:
                    await OutputSpeedscope(stackSource, traceFile, outputFile, cancellationToken).ConfigureAwait(false);
                    break;
                case StackOutputFormat.Json:
                    await OutputJson(stackSource, top, outputFile, groupBy, sortByInclusive, stackSourceKind, cancellationToken).ConfigureAwait(false);
                    break;
                case StackOutputFormat.Text:
                default:
                    OutputText(stackSource, top, outputFile, groupBy, sortByInclusive, stackSourceKind);
                    break;
            }

        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
        }
    }

    private static void OutputText(StackSource stackSource, int top, FileInfo? outputFile, GroupBy groupBy,
        bool sortByInclusive, StackSourceKind stackSourceKind)
    {
        var writer = outputFile != null ? new StreamWriter(outputFile.FullName) : Console.Out;
        
        try
        {
            var callTree = new CallTree(ScalingPolicyKind.TimeMetric);
            callTree.StackSource = stackSource;

            string sourceName = StackSourceFactory.GetDisplayName(stackSourceKind);
            writer.WriteLine($"=== {sourceName} Stacks Analysis ===");
            writer.WriteLine();
            writer.WriteLine($"Total Samples: {callTree.Root.InclusiveCount:N0}");
            writer.WriteLine($"Total Metric: {callTree.Root.InclusiveMetric:F1} ms");
            writer.WriteLine();

            var methods = AggregateMethods(stackSource, groupBy);

            if (methods.Count == 0)
            {
                writer.WriteLine($"No {sourceName.ToLowerInvariant()} samples found in trace.");
                return;
            }

            // Sort and take top N
            var sorted = sortByInclusive
                ? methods.OrderByDescending(m => m.Inclusive).Take(top).ToList()
                : methods.OrderByDescending(m => m.Exclusive).Take(top).ToList();

            var groupLabel = groupBy switch
            {
                GroupBy.Module => "Modules",
                GroupBy.Namespace => "Namespaces",
                _ => "Methods"
            };

            var sortLabel = sortByInclusive ? "Inclusive" : "Exclusive";
            var percentLabel = sortByInclusive ? "Inc %" : "Exc %";
            writer.WriteLine($"Top {Math.Min(top, sorted.Count)} {groupLabel} by {sortLabel} Time:");
            writer.WriteLine(new string('-', 90));
            writer.WriteLine($"{"Exclusive",12} {"Inclusive",12} {percentLabel,6}  {groupBy}");
            writer.WriteLine(new string('-', 90));

            var totalTime = callTree.Root.InclusiveMetric;
            foreach (var item in sorted)
            {
                var metric = sortByInclusive ? item.Inclusive : item.Exclusive;
                var pct = totalTime > 0 ? (metric / totalTime) * 100 : 0;
                var name = item.Name.Length > 55 ? item.Name.Substring(0, 52) + "..." : item.Name;
                writer.WriteLine($"{item.Exclusive,12:F1} {item.Inclusive,12:F1} {pct,5:F1}%  {name}");
            }
        }
        finally
        {
            if (outputFile != null)
                writer.Dispose();
        }
    }

    private static List<(string Name, float Exclusive, float Inclusive)> AggregateMethods(
        StackSource stackSource,
        GroupBy groupBy)
    {
        var metrics = new Dictionary<string, (float Exclusive, float Inclusive)>();
        stackSource.ForEach(sample =>
        {
            var seen = new HashSet<string>();
            bool isLeaf = true;
            var stackIndex = sample.StackIndex;
            while (stackIndex != StackSourceCallStackIndex.Invalid)
            {
                var frameIndex = stackSource.GetFrameIndex(stackIndex);
                string frameName = stackSource.GetFrameName(frameIndex, false);
                stackIndex = stackSource.GetCallerIndex(stackIndex);

                if (IsContainerFrame(frameName))
                    continue;

                string key = groupBy switch
                {
                    GroupBy.Module => ExtractModule(frameName),
                    GroupBy.Namespace => ExtractNamespace(frameName),
                    _ => frameName
                };

                metrics.TryGetValue(key, out var current);
                if (isLeaf && !IsMetricFrame(frameName))
                {
                    current.Exclusive += sample.Metric;
                    isLeaf = false;
                }

                if (seen.Add(key))
                    current.Inclusive += sample.Metric;

                metrics[key] = current;
            }
        });

        return metrics.Select(kvp => (kvp.Key, kvp.Value.Exclusive, kvp.Value.Inclusive)).ToList();
    }

    private static string ExtractModule(string methodName)
    {
        // Method names look like: "module!Namespace.Class.Method(args)" or "?!?" or "Thread (123)"
        if (string.IsNullOrEmpty(methodName))
            return "[Unknown]";

        // Handle special cases
        if (methodName.StartsWith("Thread (") || methodName.StartsWith("Process"))
            return "[Runtime]";
        if (methodName == "?!?")
            return "[Native/Unknown]";

        // Extract module before the !
        var bangIndex = methodName.IndexOf('!');
        if (bangIndex > 0)
        {
            return methodName.Substring(0, bangIndex);
        }

        return "[Unknown]";
    }

    private static string ExtractNamespace(string methodName)
    {
        // Method names look like: "module!Namespace.SubNs.Class.Method(args)"
        if (string.IsNullOrEmpty(methodName))
            return "[Unknown]";

        // Handle special cases
        if (methodName.StartsWith("Thread (") || methodName.StartsWith("Process"))
            return "[Runtime]";
        if (methodName == "?!?")
            return "[Native/Unknown]";

        // Extract after the !
        var bangIndex = methodName.IndexOf('!');
        if (bangIndex < 0)
            return "[Unknown]";

        var fullName = methodName.Substring(bangIndex + 1);
        
        // Remove method signature (everything after '(')
        var parenIndex = fullName.IndexOf('(');
        if (parenIndex > 0)
            fullName = fullName.Substring(0, parenIndex);

        // Split by '.' and take all but the last two parts (Class.Method)
        var parts = fullName.Split('.');
        if (parts.Length <= 2)
            return parts[0]; // Just return what we have

        // Take everything except Class.Method (last two parts)
        return string.Join(".", parts.Take(parts.Length - 2));
    }

    private static bool IsContainerFrame(string name) =>
        name == "ROOT"
        || name == "Threads"
        || name.StartsWith("Process", StringComparison.Ordinal)
        || name.StartsWith("Thread (", StringComparison.Ordinal);

    private static bool IsMetricFrame(string name) =>
        name.StartsWith("CPU_TIME", StringComparison.Ordinal)
        || name.StartsWith("BLOCKED_TIME", StringComparison.Ordinal)
        || name.StartsWith("AWAIT_TIME", StringComparison.Ordinal)
        || name.StartsWith("UNKNOWN_ASYNC", StringComparison.Ordinal)
        || name.StartsWith("DISK_TIME", StringComparison.Ordinal)
        || name.StartsWith("HARD_FAULT", StringComparison.Ordinal)
        || name.StartsWith("READIED_TIME", StringComparison.Ordinal)
        || name.StartsWith("NETWORK_TIME", StringComparison.Ordinal);

    private static async Task OutputJson(StackSource stackSource, int top, FileInfo? outputFile, GroupBy groupBy,
        bool sortByInclusive, StackSourceKind stackSourceKind, CancellationToken cancellationToken)
    {
        var callTree = new CallTree(ScalingPolicyKind.TimeMetric);
        callTree.StackSource = stackSource;

        var methods = AggregateMethods(stackSource, groupBy);

        var sorted = sortByInclusive
            ? methods.OrderByDescending(m => m.Inclusive).Take(top).ToList()
            : methods.OrderByDescending(m => m.Exclusive).Take(top).ToList();

        var totalTime = callTree.Root.InclusiveMetric;
        var items = sorted.Select(m => new CpuStackEntry(
            m.Name,
            Math.Round((double)m.Exclusive, 2),
            Math.Round((double)m.Inclusive, 2),
            Math.Round(totalTime > 0 ? (double)(m.Exclusive / totalTime) * 100 : 0, 2))).ToList();

        var result = new CpuStacksResponse(
            (int)callTree.Root.InclusiveCount,
            Math.Round((double)callTree.Root.InclusiveMetric, 2),
            groupBy.ToString().ToLower(),
            items,
            0,
            0,
            StackSourceFactory.GetName(stackSourceKind));

        if (outputFile != null)
            await JsonOutput.WriteToFileAsync(result, outputFile.FullName, CpuStacksJsonContext.Default.CpuStacksResponse, cancellationToken).ConfigureAwait(false);
        else
            await JsonOutput.WriteAsync(result, CpuStacksJsonContext.Default.CpuStacksResponse, cancellationToken).ConfigureAwait(false);
    }

    private static async Task OutputSpeedscope(StackSource stackSource, FileInfo traceFile, FileInfo? outputFile, CancellationToken cancellationToken)
    {
        var outputPath = outputFile?.FullName ?? Path.ChangeExtension(traceFile.FullName, ".speedscope.json");
        
        // WriteStackViewAsJson doesn't accept a cancellation token, so once it starts it runs to
        // completion. Check before we begin so a late cancel still bails out cheaply.
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => SpeedScopeStackSourceWriter.WriteStackViewAsJson(stackSource, outputPath), cancellationToken).ConfigureAwait(false);
        
        Console.Error.WriteLine($"SpeedScope file written to: {outputPath}");
        Console.Error.WriteLine("Open at: https://www.speedscope.app/");
    }
}

public enum StackOutputFormat
{
    Text,
    Json,
    Speedscope
}
