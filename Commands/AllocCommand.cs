using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing.Etlx;
using EtlxTraceLog = Microsoft.Diagnostics.Tracing.Etlx.TraceLog;

namespace PVAnalyze.Commands;

internal class AllocationInfo
{
    public string Name { get; set; } = "";
    public long Count { get; set; }
    public long TotalBytes { get; set; }
    public long LargeObjectCount { get; set; }
    public long LargeObjectBytes { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AllocationsResponse))]
internal partial class AllocJsonContext : JsonSerializerContext { }

public static class AllocCommand
{
    public static Command Create()
    {
        var command = new Command("alloc", "Analyze memory allocations by type");

        var traceFileArg = new Argument<FileInfo>("trace-file")
        {
            Description = "Path to a .nettrace, .etl, .etl.zip, or .etlx file"
        };
        var formatOption = new Option<string>("--format")
        {
            DefaultValueFactory = _ => "text",
            Description = "Output format: text, json"
        };
        var topOption = new Option<int>("--top")
        {
            DefaultValueFactory = _ => 20,
            Description = "Number of types to show"
        };
        var processOption = new Option<string?>("--process")
        {
            Description = "Filter by process name"
        };
        var fromOption = new Option<double?>("--from")
        {
            Description = "Start time in milliseconds"
        };
        var toOption = new Option<double?>("--to")
        {
            Description = "End time in milliseconds"
        };
        var groupByOption = new Option<string>("--group-by")
        {
            DefaultValueFactory = _ => "type",
            Description = "Group by: type, namespace, module"
        };

        command.Arguments.Add(traceFileArg);
        command.Options.Add(formatOption);
        command.Options.Add(topOption);
        command.Options.Add(processOption);
        command.Options.Add(fromOption);
        command.Options.Add(toOption);
        command.Options.Add(groupByOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var traceFile = parseResult.GetValue(traceFileArg)!;
            var format = parseResult.GetValue(formatOption)!;
            var top = parseResult.GetValue(topOption)!;
            var processName = parseResult.GetValue(processOption)!;
            var fromMs = parseResult.GetValue(fromOption)!;
            var toMs = parseResult.GetValue(toOption)!;
            var groupBy = parseResult.GetValue(groupByOption)!;
            await Execute(traceFile, format, top, processName, fromMs, toMs, groupBy, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(FileInfo traceFile, string format, int top, string? processName, double? fromMs, double? toMs, string groupBy, CancellationToken cancellationToken)
    {
        if (!traceFile.Exists)
        {
            Console.Error.WriteLine($"File not found: {traceFile.FullName}");
            return;
        }

        try
        {
            string etlxPath = await EtlxCache.GetOrCreateEtlxAsync(traceFile.FullName, cancellationToken).ConfigureAwait(false);
            using var traceLog = new EtlxTraceLog(etlxPath);
            
            var allocations = new Dictionary<string, AllocationInfo>();
            long totalBytes = 0;
            long totalCount = 0;

            foreach (var evt in traceLog.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Look for allocation events (AllocationTick has type info, SampledObjectAllocation is higher frequency)
                if (evt.EventName != "GC/AllocationTick" && evt.EventName != "GC/SampledObjectAllocation") continue;

                // Time filtering
                double timeMs = evt.TimeStampRelativeMSec;
                if (fromMs.HasValue && timeMs < fromMs.Value) continue;
                if (toMs.HasValue && timeMs > toMs.Value) continue;

                // Process filtering
                if (!string.IsNullOrEmpty(processName) && 
                    !evt.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Extract type name and size from payload
                string? typeName = null;
                long size = 0;
                bool isLargeObject = false;

                try
                {
                    // Try to get TypeName (AllocationTick V2+ and SampledObjectAllocation both have it)
                    typeName = evt.PayloadStringByName("TypeName");
                    
                    if (evt.EventName == "GC/AllocationTick")
                    {
                        // AllocationTick: PayloadNames: AllocationAmount, AllocationKind, ClrInstanceID, AllocationAmount64, TypeID, TypeName, HeapIndex, Address
                        var amount64 = evt.PayloadByName("AllocationAmount64");
                        if (amount64 != null)
                            size = Convert.ToInt64(amount64);
                        else
                        {
                            var amount = evt.PayloadByName("AllocationAmount");
                            if (amount != null)
                                size = Convert.ToInt64(amount);
                        }

                        var kind = evt.PayloadByName("AllocationKind");
                        if (kind != null)
                            isLargeObject = Convert.ToInt32(kind) == 1; // 1 = Large
                    }
                    else // GC/SampledObjectAllocation
                    {
                        // SampledObjectAllocation: PayloadNames: Address, TypeID, ObjectCountForTypeSample, TotalSizeForTypeSample, TypeName
                        var totalSize = evt.PayloadByName("TotalSizeForTypeSample");
                        if (totalSize != null)
                            size = Convert.ToInt64(totalSize);
                        
                        // Large object if size > 85000 bytes
                        isLargeObject = size > 85000;
                    }
                }
                catch
                {
                    continue; // Skip malformed events
                }

                if (string.IsNullOrEmpty(typeName) || size <= 0) continue;

                string key = groupBy.ToLowerInvariant() switch
                {
                    "namespace" => ExtractNamespace(typeName),
                    "module" => ExtractModule(typeName),
                    _ => typeName
                };

                if (!allocations.TryGetValue(key, out var info))
                {
                    info = new AllocationInfo { Name = key };
                    allocations[key] = info;
                }
                info.Count++;
                info.TotalBytes += size;
                if (isLargeObject)
                {
                    info.LargeObjectCount++;
                    info.LargeObjectBytes += size;
                }

                totalBytes += size;
                totalCount++;
            }

            if (totalCount == 0)
            {
                Console.WriteLine("No allocation events found in trace.");
                Console.WriteLine("Hint: Collect with allocation tracking enabled:");
                Console.WriteLine("  dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x200001:5 -- dotnet run");
                return;
            }

            if (format == "json")
            {
                await OutputJson(allocations, totalBytes, totalCount, top, groupBy, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                OutputText(allocations, totalBytes, totalCount, top, groupBy);
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

    private static string ExtractNamespace(string typeName)
    {
        // Handle generic types like System.Collections.Generic.List`1[System.String]
        int genericIdx = typeName.IndexOf('`');
        if (genericIdx > 0)
            typeName = typeName.Substring(0, genericIdx);
        
        int bracketIdx = typeName.IndexOf('[');
        if (bracketIdx > 0)
            typeName = typeName.Substring(0, bracketIdx);

        int lastDot = typeName.LastIndexOf('.');
        if (lastDot > 0)
            return typeName.Substring(0, lastDot);
        
        return typeName;
    }

    private static string ExtractModule(string typeName)
    {
        // For types, module is typically the first part of the namespace
        int firstDot = typeName.IndexOf('.');
        if (firstDot > 0)
            return typeName.Substring(0, firstDot);
        return typeName;
    }

    private static void OutputText(Dictionary<string, AllocationInfo> allocations, 
        long totalBytes, long totalCount, int top, string groupBy)
    {
        Console.WriteLine("=== Allocation Analysis ===");
        Console.WriteLine();
        Console.WriteLine($"Total Allocations: {totalCount:N0}");
        Console.WriteLine($"Total Bytes: {FormatBytes(totalBytes)}");
        Console.WriteLine();

        var sorted = allocations.Values
            .OrderByDescending(a => a.TotalBytes)
            .Take(top)
            .ToList();

        string label = groupBy.ToLowerInvariant() switch
        {
            "namespace" => "Namespace",
            "module" => "Module",
            _ => "Type"
        };

        Console.WriteLine($"Top {Math.Min(top, sorted.Count)} {label}s by Allocated Bytes:");
        Console.WriteLine(new string('-', 100));
        Console.WriteLine($"{"Count",12} {"Bytes",14} {"Avg Size",10} {"LOH",8}  {label}");
        Console.WriteLine(new string('-', 100));

        foreach (var alloc in sorted)
        {
            double avgSize = alloc.Count > 0 ? (double)alloc.TotalBytes / alloc.Count : 0;
            string loh = alloc.LargeObjectCount > 0 ? $"{alloc.LargeObjectCount:N0}" : "-";
            Console.WriteLine($"{alloc.Count,12:N0} {FormatBytes(alloc.TotalBytes),14} {FormatBytes((long)avgSize),10} {loh,8}  {TruncateName(alloc.Name, 50)}");
        }
    }

    private static async Task OutputJson(Dictionary<string, AllocationInfo> allocations,
        long totalBytes, long totalCount, int top, string groupBy, CancellationToken cancellationToken)
    {
        var sorted = allocations.Values
            .OrderByDescending(a => a.TotalBytes)
            .Take(top)
            .ToList();

        var response = new AllocationsResponse(
            totalCount,
            totalBytes,
            groupBy,
            sorted.Select(a => new AllocationEntry(
                a.Name,
                a.Count,
                a.TotalBytes,
                a.Count > 0 ? a.TotalBytes / (double)a.Count : 0,
                a.LargeObjectCount,
                a.LargeObjectBytes)).ToList());

        await JsonOutput.WriteAsync(response, AllocJsonContext.Default.AllocationsResponse, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824)
            return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576)
            return $"{bytes / 1_048_576.0:F2} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F2} KB";
        return $"{bytes} B";
    }

    private static string TruncateName(string name, int maxLen)
    {
        if (name.Length <= maxLen) return name;
        return "..." + name.Substring(name.Length - maxLen + 3);
    }
}
