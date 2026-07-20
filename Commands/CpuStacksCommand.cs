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
            Description = "Path to the .nettrace file to analyze"
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
        var pidOption = new Option<int?>("--pid")
        {
            Description = "Filter CPU samples by process ID"
        };

        var command = new Command("cpustacks", "Analyze CPU stacks from a trace")
        {
            traceFileArg,
            formatOption,
            topOption,
            outputOption,
            groupByOption,
            fromOption,
            toOption,
            inclusiveOption,
            pidOption
        };

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
            var pid = parseResult.GetValue(pidOption)!;
            await Execute(
                traceFile,
                format,
                top,
                outputFile,
                groupBy,
                fromMs,
                toMs,
                inclusive,
                pid,
                cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(FileInfo traceFile, StackOutputFormat format, int top, FileInfo? outputFile,
        GroupBy groupBy, double? fromMs, double? toMs, bool sortByInclusive,
        int? processId, CancellationToken cancellationToken)
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
            
            // Get events, optionally filtered by time
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

            if (processId.HasValue)
            {
                events = events.Filter(evt => evt.ProcessID == processId.Value);
            }
            
            // Create stack source from events
            var traceStackSource = new TraceEventStackSource(events);
            
            // Clone for stable access
            var stackSource = CopyStackSource.Clone(traceStackSource);

            switch (format)
            {
                case StackOutputFormat.Speedscope:
                    await OutputSpeedscope(stackSource, traceFile, outputFile, cancellationToken).ConfigureAwait(false);
                    break;
                case StackOutputFormat.Json:
                    await OutputJson(stackSource, top, outputFile, groupBy, sortByInclusive, cancellationToken).ConfigureAwait(false);
                    break;
                case StackOutputFormat.Text:
                default:
                    OutputText(stackSource, top, outputFile, groupBy, sortByInclusive);
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

    private static void OutputText(StackSource stackSource, int top, FileInfo? outputFile, GroupBy groupBy, bool sortByInclusive)
    {
        var writer = outputFile != null ? new StreamWriter(outputFile.FullName) : Console.Out;
        
        try
        {
            var callTree = new CallTree(ScalingPolicyKind.TimeMetric);
            callTree.StackSource = stackSource;

            writer.WriteLine("=== CPU Stacks Analysis ===");
            writer.WriteLine();
            writer.WriteLine($"Total Samples: {callTree.Root.InclusiveCount:N0}");
            writer.WriteLine($"Total CPU Time: {callTree.Root.InclusiveMetric:F1} ms");
            writer.WriteLine();

            // Collect all methods
            var methods = new List<(string Name, float Exclusive, float Inclusive)>();
            CollectMethods(callTree.Root, methods);

            if (methods.Count == 0)
            {
                writer.WriteLine("No CPU samples found in trace.");
                writer.WriteLine("Ensure the trace was collected with CPU sampling enabled.");
                return;
            }

            // Group if needed
            var grouped = GroupMethods(methods, groupBy);

            // Sort and take top N
            var sorted = sortByInclusive
                ? grouped.OrderByDescending(m => m.Inclusive).Take(top).ToList()
                : grouped.OrderByDescending(m => m.Exclusive).Take(top).ToList();

            var groupLabel = groupBy switch
            {
                GroupBy.Module => "Modules",
                GroupBy.Namespace => "Namespaces",
                _ => "Methods"
            };

            var sortLabel = sortByInclusive ? "Inclusive" : "Exclusive";
            writer.WriteLine($"Top {Math.Min(top, sorted.Count)} {groupLabel} by {sortLabel} CPU Time:");
            writer.WriteLine(new string('-', 90));
            writer.WriteLine($"{"Exclusive",12} {"Inclusive",12} {"%",6}  {groupBy}");
            writer.WriteLine(new string('-', 90));

            var totalTime = callTree.Root.InclusiveMetric;
            foreach (var item in sorted)
            {
                var pct = totalTime > 0 ? (item.Exclusive / totalTime) * 100 : 0;
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

    private static List<(string Name, float Exclusive, float Inclusive)> GroupMethods(
        List<(string Name, float Exclusive, float Inclusive)> methods, GroupBy groupBy)
    {
        if (groupBy == GroupBy.Method)
            return methods;

        var grouped = new Dictionary<string, (float Exclusive, float Inclusive)>();

        foreach (var method in methods)
        {
            var key = groupBy switch
            {
                GroupBy.Module => ExtractModule(method.Name),
                GroupBy.Namespace => ExtractNamespace(method.Name),
                _ => method.Name
            };

            if (!grouped.TryGetValue(key, out var current))
            {
                current = (0, 0);
            }
            grouped[key] = (current.Exclusive + method.Exclusive, current.Inclusive + method.Inclusive);
        }

        return grouped.Select(kvp => (kvp.Key, kvp.Value.Exclusive, kvp.Value.Inclusive)).ToList();
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

    private static void CollectMethods(CallTreeNode node, List<(string Name, float Exclusive, float Inclusive)> methods)
    {
        if (node.ExclusiveMetric > 0)
        {
            methods.Add((node.Name, node.ExclusiveMetric, node.InclusiveMetric));
        }

        foreach (var child in node.Callees ?? Enumerable.Empty<CallTreeNode>())
        {
            CollectMethods(child, methods);
        }
    }

    private static async Task OutputJson(StackSource stackSource, int top, FileInfo? outputFile, GroupBy groupBy, bool sortByInclusive, CancellationToken cancellationToken)
    {
        var callTree = new CallTree(ScalingPolicyKind.TimeMetric);
        callTree.StackSource = stackSource;

        var methods = new List<(string Name, float Exclusive, float Inclusive)>();
        CollectMethods(callTree.Root, methods);

        var grouped = GroupMethods(methods, groupBy);

        var sorted = sortByInclusive
            ? grouped.OrderByDescending(m => m.Inclusive).Take(top).ToList()
            : grouped.OrderByDescending(m => m.Exclusive).Take(top).ToList();

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
            0);

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
