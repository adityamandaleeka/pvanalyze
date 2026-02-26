using System.CommandLine;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

public static class GcStatsCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file", "Path to the .nettrace file to analyze");
        var formatOption = new Option<OutputFormat>("--format", () => OutputFormat.Text, "Output format");
        var processOption = new Option<string?>("--process", "Filter by process name");
        var timelineOption = new Option<bool>("--timeline", "Show per-GC timeline");
        var longestOption = new Option<int?>("--longest", "Show N longest GC pauses");
        var fromOption = new Option<double?>("--from", "Start time in milliseconds");
        var toOption = new Option<double?>("--to", "End time in milliseconds");

        var command = new Command("gcstats", "Display GC statistics from a trace")
        {
            traceFileArg,
            formatOption,
            processOption,
            timelineOption,
            longestOption,
            fromOption,
            toOption
        };

        command.SetHandler(Execute, traceFileArg, formatOption, processOption, timelineOption, longestOption, fromOption, toOption);
        return command;
    }

    private static void Execute(FileInfo traceFile, OutputFormat format, string? processFilter,
        bool timeline, int? longest, double? fromMs, double? toMs)
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
            
            // Set up GC analysis
            using var source = traceLog.Events.GetSource();
            TraceLoadedDotNetRuntimeExtensions.NeedLoadedDotNetRuntimes(source);
            source.Process();

            var allGcEvents = new List<GcEventInfo>();
            var processStats = new List<GcProcessStats>();

            foreach (var process in TraceProcessesExtensions.Processes(source))
            {
                var runtime = TraceLoadedDotNetRuntimeExtensions.LoadedDotNetRuntime(process);
                if (runtime == null || runtime.GC.Stats() == null)
                    continue;

                if (processFilter != null && 
                    !process.Name.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var gcStats = runtime.GC.Stats();
                
                // Collect individual GC events (filtered by time if specified)
                int gen0 = 0, gen1 = 0, gen2 = 0;
                double filteredPauseTime = 0;
                int filteredCount = 0;

                foreach (var gc in runtime.GC.GCs)
                {
                    // Time filtering
                    if (fromMs.HasValue && gc.StartRelativeMSec < fromMs.Value) continue;
                    if (toMs.HasValue && gc.StartRelativeMSec > toMs.Value) continue;

                    filteredCount++;
                    filteredPauseTime += gc.PauseDurationMSec;

                    switch (gc.Generation)
                    {
                        case 0: gen0++; break;
                        case 1: gen1++; break;
                        case 2: gen2++; break;
                    }

                    allGcEvents.Add(new GcEventInfo
                    {
                        ProcessId = process.ProcessID,
                        ProcessName = process.Name,
                        GcNumber = gc.Number,
                        Generation = gc.Generation,
                        Type = gc.Type.ToString(),
                        Reason = gc.Reason.ToString(),
                        StartTimeMs = Math.Round(gc.StartRelativeMSec, 3),
                        PauseDurationMs = Math.Round(gc.PauseDurationMSec, 3),
                        HeapSizeBeforeMB = Math.Round(gc.HeapSizeBeforeMB, 2),
                        HeapSizeAfterMB = Math.Round(gc.HeapSizeAfterMB, 2),
                        PromotedMB = Math.Round(gc.PromotedMB, 2)
                    });
                }

                // Only add process if it has GCs in the time window
                if (filteredCount > 0)
                {
                    processStats.Add(new GcProcessStats
                    {
                        ProcessId = process.ProcessID,
                        ProcessName = process.Name,
                        TotalGCs = filteredCount,
                        TotalAllocatedMB = gcStats.TotalAllocatedMB,
                        TotalGcCpuMSec = gcStats.TotalCpuMSec,
                        TotalPauseTimeMSec = filteredPauseTime,
                        MaxHeapSizeMB = gcStats.MaxSizePeakMB,
                        PauseTimePercent = gcStats.GetGCPauseTimePercentage(),
                        Gen0Count = gen0,
                        Gen1Count = gen1,
                        Gen2Count = gen2,
                        HeapCount = gcStats.HeapCount
                    });
                }
            }

            if (processStats.Count == 0)
            {
                Console.Error.WriteLine("No .NET processes with GC data found in trace.");
                Console.Error.WriteLine("Ensure the trace was collected with GC events enabled.");
                try { File.Delete(etlxPath); } catch { }
                return;
            }

            // Determine output mode
            if (timeline || longest.HasValue)
            {
                OutputTimeline(allGcEvents, format, longest);
            }
            else
            {
                OutputSummary(processStats, format);
            }

            // Clean up
            try { File.Delete(etlxPath); } catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
        }
    }

    private static void OutputSummary(List<GcProcessStats> processes, OutputFormat format)
    {
        processes = processes.OrderByDescending(p => p.TotalPauseTimeMSec).ToList();

        if (format == OutputFormat.Json)
        {
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            Console.WriteLine(JsonSerializer.Serialize(processes, options));
        }
        else
        {
            foreach (var proc in processes)
            {
                Console.WriteLine($"=== GC Stats for {proc.ProcessName} (PID {proc.ProcessId}) ===");
                Console.WriteLine();
                Console.WriteLine($"  Total GCs:           {proc.TotalGCs}");
                Console.WriteLine($"    Gen 0:             {proc.Gen0Count}");
                Console.WriteLine($"    Gen 1:             {proc.Gen1Count}");
                Console.WriteLine($"    Gen 2:             {proc.Gen2Count}");
                Console.WriteLine();
                Console.WriteLine($"  Total Allocated:     {proc.TotalAllocatedMB:F2} MB");
                Console.WriteLine($"  Max Heap Size:       {proc.MaxHeapSizeMB:F2} MB");
                Console.WriteLine($"  Heap Count:          {proc.HeapCount}");
                Console.WriteLine();
                Console.WriteLine($"  Total GC CPU Time:   {proc.TotalGcCpuMSec:F1} ms");
                Console.WriteLine($"  Total Pause Time:    {proc.TotalPauseTimeMSec:F1} ms");
                Console.WriteLine($"  % Time in GC:        {proc.PauseTimePercent:F1}%");
                Console.WriteLine();
            }
        }
    }

    private static void OutputTimeline(List<GcEventInfo> gcEvents, OutputFormat format, int? longest)
    {
        // Sort by pause duration if showing longest, otherwise by time
        var events = longest.HasValue
            ? gcEvents.OrderByDescending(e => e.PauseDurationMs).Take(longest.Value).ToList()
            : gcEvents.OrderBy(e => e.StartTimeMs).ToList();

        if (format == OutputFormat.Json)
        {
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            Console.WriteLine(JsonSerializer.Serialize(events, options));
        }
        else
        {
            var title = longest.HasValue ? $"Top {longest} Longest GC Pauses" : "GC Timeline";
            Console.WriteLine($"=== {title} ===");
            Console.WriteLine();
            Console.WriteLine($"{"#",5} {"Gen",4} {"Type",-12} {"Reason",-20} {"Start (ms)",12} {"Pause (ms)",12} {"Before MB",10} {"After MB",10}");
            Console.WriteLine(new string('-', 100));

            foreach (var gc in events)
            {
                Console.WriteLine($"{gc.GcNumber,5} {gc.Generation,4} {Truncate(gc.Type, 12),-12} {Truncate(gc.Reason, 20),-20} {gc.StartTimeMs,12:F1} {gc.PauseDurationMs,12:F2} {gc.HeapSizeBeforeMB,10:F1} {gc.HeapSizeAfterMB,10:F1}");
            }
            Console.WriteLine();
            Console.WriteLine($"Total: {events.Count} GCs, {events.Sum(e => e.PauseDurationMs):F1} ms pause time");
        }
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= maxLen ? s : s.Substring(0, maxLen - 3) + "...";
    }

    private class GcProcessStats
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public int TotalGCs { get; set; }
        public double TotalAllocatedMB { get; set; }
        public double TotalGcCpuMSec { get; set; }
        public double TotalPauseTimeMSec { get; set; }
        public double MaxHeapSizeMB { get; set; }
        public double PauseTimePercent { get; set; }
        public int Gen0Count { get; set; }
        public int Gen1Count { get; set; }
        public int Gen2Count { get; set; }
        public int HeapCount { get; set; }
    }

    private class GcEventInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public int GcNumber { get; set; }
        public int Generation { get; set; }
        public string Type { get; set; } = "";
        public string Reason { get; set; } = "";
        public double StartTimeMs { get; set; }
        public double PauseDurationMs { get; set; }
        public double HeapSizeBeforeMB { get; set; }
        public double HeapSizeAfterMB { get; set; }
        public double PromotedMB { get; set; }
    }
}

public enum OutputFormat
{
    Text,
    Json
}
