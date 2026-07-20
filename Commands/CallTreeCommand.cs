using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing.Stacks;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using PVAnalyze;

namespace PVAnalyze.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CallTreeResponse))]
[JsonSerializable(typeof(CallerCalleeResponse))]
internal partial class CallTreeJsonContext : JsonSerializerContext { }

public static class CallTreeCommand
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
        var depthOption = new Option<int>("--depth")
        {
            DefaultValueFactory = _ => 3,
            Description = "Max depth to display"
        };
        var hotPathOption = new Option<bool>("--hot-path")
        {
            Description = "Follow the hot path (dominant call chain)"
        };
        var callerCalleeOption = new Option<string?>("--caller-callee")
        {
            Description = "Show callers and callees for the specified method name"
        };
        var fromOption = new Option<double?>("--from")
        {
            Description = "Start time in milliseconds"
        };
        var toOption = new Option<double?>("--to")
        {
            Description = "End time in milliseconds"
        };
        var minPercentOption = new Option<double>("--min-percent")
        {
            DefaultValueFactory = _ => 1.0,
            Description = "Hide nodes below this inclusive % threshold"
        };
        var stackSourceOption = new Option<string>("--stack-source")
        {
            DefaultValueFactory = _ => "cpu",
            Description = "Stack source: cpu, threadtime, activity, activity-cpu, or activity-threadtime"
        };
        stackSourceOption.AcceptOnlyFromAmong(
            "cpu", "threadtime", "activity", "activity-cpu", "activity-threadtime");

        var command = new Command("calltree", "Call tree analysis with hot path detection")
        {
            traceFileArg,
            formatOption,
            depthOption,
            hotPathOption,
            callerCalleeOption,
            fromOption,
            toOption,
            minPercentOption,
            stackSourceOption
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var traceFile = parseResult.GetValue(traceFileArg)!;
            var format = parseResult.GetValue(formatOption)!;
            var depth = parseResult.GetValue(depthOption)!;
            var hotPath = parseResult.GetValue(hotPathOption)!;
            var callerCallee = parseResult.GetValue(callerCalleeOption)!;
            var fromMs = parseResult.GetValue(fromOption)!;
            var toMs = parseResult.GetValue(toOption)!;
            var minPercent = parseResult.GetValue(minPercentOption)!;
            var stackSource = StackSourceFactory.ParseKind(parseResult.GetValue(stackSourceOption)!);
            await Execute(traceFile, format, depth, hotPath, callerCallee, fromMs, toMs, minPercent, stackSource, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(FileInfo traceFile, OutputFormat format, int depth,
        bool hotPath, string? callerCallee, double? fromMs, double? toMs, double minPercent,
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

            var callTree = new CallTree(ScalingPolicyKind.TimeMetric);
            callTree.StackSource = stackSource;

            if (!string.IsNullOrEmpty(callerCallee))
            {
                await OutputCallerCallee(callTree, callerCallee!, format, cancellationToken).ConfigureAwait(false);
            }
            else if (hotPath)
            {
                await OutputHotPath(callTree, format, stackSourceKind, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await OutputCallTree(callTree, depth, format, minPercent, stackSourceKind, cancellationToken).ConfigureAwait(false);
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

    private static async Task OutputCallTree(CallTree callTree, int maxDepth, OutputFormat format,
        double minPercent, StackSourceKind stackSourceKind, CancellationToken cancellationToken)
    {
        var result = TraceAnalyzer.GetCallTree(callTree, maxDepth);
        var unfilteredCount = CountNodes(result.Nodes);

        if (minPercent > 0)
            result = result with { Nodes = FilterByMinPercent(result.Nodes, minPercent) };

        var filteredCount = CountNodes(result.Nodes);

        if (format == OutputFormat.Json)
        {
            await JsonOutput.WriteAsync(result, CallTreeJsonContext.Default.CallTreeResponse, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Console.WriteLine($"=== {StackSourceFactory.GetDisplayName(stackSourceKind)} Call Tree ===");
            Console.WriteLine($"Total: {result.TotalMetricMs:F1} ms ({result.TotalSamples:N0} samples)");
            if (minPercent > 0)
                Console.WriteLine($"Hiding nodes below {minPercent:G}% inclusive ({filteredCount}/{unfilteredCount} nodes shown). Adjust with --min-percent <value> or use --min-percent 0 to show all.");
            Console.WriteLine();
            Console.WriteLine($"{"Inc %",7} {"Exc %",7} {"Inc (ms)",10}  Name");
            Console.WriteLine(new string('─', 90));
            PrintTreeText(result.Nodes, 0);
        }
    }

    private static void PrintTreeText(List<CallTreeNodeDto> nodes, int indent)
    {
        foreach (var node in nodes)
        {
            var prefix = new string(' ', indent * 2);
            var arrow = node.Children?.Count > 0 ? "├─" : "└─";
            if (indent == 0) arrow = "";
            Console.WriteLine($"{node.InclusivePercent,7:F1} {node.ExclusivePercent,7:F1} {node.InclusiveMs,10:F1}  {prefix}{arrow}{Truncate(node.Name, 70 - indent * 2)}");

            if (node.Children != null)
                PrintTreeText(node.Children, indent + 1);
        }
    }

    private static async Task OutputHotPath(CallTree callTree, OutputFormat format,
        StackSourceKind stackSourceKind, CancellationToken cancellationToken)
    {
        // Start from root (path = [0] = first real child)
        var result = TraceAnalyzer.GetHotPath(callTree, new[] { 0 });

        if (format == OutputFormat.Json)
        {
            await JsonOutput.WriteAsync(result, CallTreeJsonContext.Default.CallTreeResponse, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Console.WriteLine($"=== Hot Path ({StackSourceFactory.GetDisplayName(stackSourceKind)}) ===");
            Console.WriteLine($"Total: {result.TotalMetricMs:F1} ms");
            Console.WriteLine("Follows the dominant call chain (child >= 80% of parent's inclusive time)");
            Console.WriteLine();
            Console.WriteLine($"{"Inc %",7} {"Exc %",7}  Path");
            Console.WriteLine(new string('─', 90));
            PrintHotPathText(result.Nodes, 0);
        }
    }

    private static void PrintHotPathText(List<CallTreeNodeDto> nodes, int depth)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var prefix = new string(' ', depth * 2);
            var marker = (i == 0 && node.Children?.Count > 0) ? "🔥" : (i == 0 ? "🎯" : "  ");
            Console.WriteLine($"{node.InclusivePercent,7:F1} {node.ExclusivePercent,7:F1}  {prefix}{marker} {Truncate(node.Name, 70 - depth * 2)}");

            if (i == 0 && node.Children != null)
                PrintHotPathText(node.Children, depth + 1);
        }
    }

    private static async Task OutputCallerCallee(CallTree callTree, string method, OutputFormat format, CancellationToken cancellationToken)
    {
        var result = TraceAnalyzer.GetCallerCallee(callTree, method);

        if (format == OutputFormat.Json)
        {
            await JsonOutput.WriteAsync(result, CallTreeJsonContext.Default.CallerCalleeResponse, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Console.WriteLine("=== Caller / Callee ===");
            Console.WriteLine();
            Console.WriteLine($"Focus: {result.Focus.Name}");
            Console.WriteLine($"  Inclusive: {result.Focus.InclusiveMs:F1} ms ({result.Focus.InclusivePercent:F1}%)");
            Console.WriteLine($"  Exclusive: {result.Focus.ExclusiveMs:F1} ms ({result.Focus.ExclusivePercent:F1}%)");
            Console.WriteLine();

            Console.WriteLine($"▲ Callers ({result.Callers.Count}):");
            Console.WriteLine($"  {"Inc %",7} {"Exc %",7} {"Inc (ms)",10}  Name");
            Console.WriteLine($"  {new string('─', 85)}");
            foreach (var c in result.Callers.Take(20))
            {
                Console.WriteLine($"  {c.InclusivePercent,7:F1} {c.ExclusivePercent,7:F1} {c.InclusiveMs,10:F1}  {Truncate(c.Name, 60)}");
            }

            Console.WriteLine();
            Console.WriteLine($"▼ Callees ({result.Callees.Count}):");
            Console.WriteLine($"  {"Inc %",7} {"Exc %",7} {"Inc (ms)",10}  Name");
            Console.WriteLine($"  {new string('─', 85)}");
            foreach (var c in result.Callees.Take(20))
            {
                Console.WriteLine($"  {c.InclusivePercent,7:F1} {c.ExclusivePercent,7:F1} {c.InclusiveMs,10:F1}  {Truncate(c.Name, 60)}");
            }
        }
    }

    private static int CountNodes(List<CallTreeNodeDto> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            count++;
            if (node.Children != null) count += CountNodes(node.Children);
        }
        return count;
    }

    private static List<CallTreeNodeDto> FilterByMinPercent(List<CallTreeNodeDto> nodes, double minPercent)
    {
        var filtered = new List<CallTreeNodeDto>();
        foreach (var node in nodes)
        {
            if (Math.Abs(node.InclusivePercent) >= minPercent)
            {
                var children = node.Children != null ? FilterByMinPercent(node.Children, minPercent) : null;
                filtered.Add(node with { Children = children });
            }
        }
        return filtered;
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= maxLen ? s : s.Substring(0, maxLen - 3) + "...";
    }
}
