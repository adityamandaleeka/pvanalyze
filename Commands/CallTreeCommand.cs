using System.CommandLine;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Stacks;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using PVAnalyze;

namespace PVAnalyze.Commands;

public static class CallTreeCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file", "Path to the .nettrace file to analyze");
        var formatOption = new Option<OutputFormat>("--format", () => OutputFormat.Text, "Output format");
        var depthOption = new Option<int>("--depth", () => 3, "Max depth to display");
        var hotPathOption = new Option<bool>("--hot-path", "Follow the hot path (dominant call chain)");
        var callerCalleeOption = new Option<string?>("--caller-callee", "Show callers and callees for the specified method name");
        var fromOption = new Option<double?>("--from", "Start time in milliseconds");
        var toOption = new Option<double?>("--to", "End time in milliseconds");
        var minPercentOption = new Option<double>("--min-percent", () => 1.0, "Hide nodes below this inclusive % threshold");

        var command = new Command("calltree", "CPU call tree analysis with hot path detection")
        {
            traceFileArg,
            formatOption,
            depthOption,
            hotPathOption,
            callerCalleeOption,
            fromOption,
            toOption,
            minPercentOption
        };

        command.SetHandler(async (context) =>
        {
            var traceFile = context.ParseResult.GetValueForArgument(traceFileArg);
            var format = context.ParseResult.GetValueForOption(formatOption);
            var depth = context.ParseResult.GetValueForOption(depthOption);
            var hotPath = context.ParseResult.GetValueForOption(hotPathOption);
            var callerCallee = context.ParseResult.GetValueForOption(callerCalleeOption);
            var fromMs = context.ParseResult.GetValueForOption(fromOption);
            var toMs = context.ParseResult.GetValueForOption(toOption);
            var minPercent = context.ParseResult.GetValueForOption(minPercentOption);
            Execute(traceFile, format, depth, hotPath, callerCallee, fromMs, toMs, minPercent);
        });
        return command;
    }

    private static void Execute(FileInfo traceFile, OutputFormat format, int depth,
        bool hotPath, string? callerCallee, double? fromMs, double? toMs, double minPercent)
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

            // Build stack source with optional time filter
            Etlx.TraceEvents events;
            if (fromMs.HasValue || toMs.HasValue)
            {
                double startMs = fromMs ?? 0;
                double endMs = toMs ?? double.MaxValue;
                events = traceLog.Events.FilterByTime(
                    traceLog.SessionStartTime + TimeSpan.FromMilliseconds(startMs),
                    traceLog.SessionStartTime + TimeSpan.FromMilliseconds(endMs));
            }
            else
            {
                events = traceLog.Events;
            }

            var traceStackSource = new TraceEventStackSource(events);
            var stackSource = CopyStackSource.Clone(traceStackSource);

            var callTree = new CallTree(ScalingPolicyKind.TimeMetric);
            callTree.StackSource = stackSource;

            if (!string.IsNullOrEmpty(callerCallee))
            {
                OutputCallerCallee(callTree, callerCallee!, format);
            }
            else if (hotPath)
            {
                OutputHotPath(callTree, format);
            }
            else
            {
                OutputCallTree(callTree, depth, format, minPercent);
            }

            try { File.Delete(etlxPath); } catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
        }
    }

    private static void OutputCallTree(CallTree callTree, int maxDepth, OutputFormat format, double minPercent)
    {
        var result = TraceAnalyzer.GetCallTree(callTree, maxDepth);
        var unfilteredCount = CountNodes(result.Nodes);

        if (minPercent > 0)
            result = result with { Nodes = FilterByMinPercent(result.Nodes, minPercent) };

        var filteredCount = CountNodes(result.Nodes);

        if (format == OutputFormat.Json)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            Console.WriteLine(JsonSerializer.Serialize(result, options));
        }
        else
        {
            Console.WriteLine("=== CPU Call Tree ===");
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

    private static void OutputHotPath(CallTree callTree, OutputFormat format)
    {
        // Start from root (path = [0] = first real child)
        var result = TraceAnalyzer.GetHotPath(callTree, new[] { 0 });

        if (format == OutputFormat.Json)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            Console.WriteLine(JsonSerializer.Serialize(result, options));
        }
        else
        {
            Console.WriteLine("=== Hot Path (CPU) ===");
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

    private static void OutputCallerCallee(CallTree callTree, string method, OutputFormat format)
    {
        var result = TraceAnalyzer.GetCallerCallee(callTree, method);

        if (format == OutputFormat.Json)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            Console.WriteLine(JsonSerializer.Serialize(result, options));
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
