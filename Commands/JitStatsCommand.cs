using System.CommandLine;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Analysis;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

public static class JitStatsCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file", "Path to the .nettrace file to analyze");
        var formatOption = new Option<OutputFormat>("--format", () => OutputFormat.Text, "Output format");
        var processOption = new Option<string?>("--process", "Filter by process name");

        var command = new Command("jitstats", "Display JIT compilation statistics from a trace")
        {
            traceFileArg,
            formatOption,
            processOption
        };

        command.SetHandler(Execute, traceFileArg, formatOption, processOption);
        return command;
    }

    private static void Execute(FileInfo traceFile, OutputFormat format, string? processFilter)
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
            
            // Set up JIT analysis
            using var source = traceLog.Events.GetSource();
            TraceLoadedDotNetRuntimeExtensions.NeedLoadedDotNetRuntimes(source);
            source.Process();

            var processes = new List<JitProcessStats>();

            foreach (var process in TraceProcessesExtensions.Processes(source))
            {
                var runtime = TraceLoadedDotNetRuntimeExtensions.LoadedDotNetRuntime(process);
                if (runtime == null || runtime.JIT.Stats() == null)
                    continue;

                if (processFilter != null && 
                    !process.Name.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var jitStats = runtime.JIT.Stats();
                
                if (!jitStats.Interesting)
                    continue;

                processes.Add(new JitProcessStats
                {
                    ProcessId = process.ProcessID,
                    ProcessName = process.Name,
                    TotalMethodsJitted = jitStats.Count,
                    TotalJitCpuTimeMSec = jitStats.TotalCpuTimeMSec,
                    TotalILSize = jitStats.TotalILSize,
                    TotalNativeSize = jitStats.TotalNativeSize,
                    ForegroundCount = jitStats.CountForeground,
                    BackgroundCount = jitStats.CountBackgroundMultiCoreJit + jitStats.CountBackgroundTieredCompilation,
                    InliningSuccesses = runtime.JIT.Stats().InliningSuccesses.Count,
                    InliningFailures = runtime.JIT.Stats().InliningFailures.Count
                });
            }

            if (processes.Count == 0)
            {
                Console.Error.WriteLine("No .NET processes with JIT data found in trace.");
                Console.Error.WriteLine("Ensure the trace was collected with JIT events enabled.");
                return;
            }

            // Sort by JIT time descending
            processes = processes.OrderByDescending(p => p.TotalJitCpuTimeMSec).ToList();

            switch (format)
            {
                case OutputFormat.Json:
                    OutputJson(processes);
                    break;
                case OutputFormat.Text:
                default:
                    OutputText(processes);
                    break;
            }

            // Clean up
            try { File.Delete(etlxPath); } catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
        }
    }

    private static void OutputText(List<JitProcessStats> processes)
    {
        foreach (var proc in processes)
        {
            Console.WriteLine($"=== JIT Stats for {proc.ProcessName} (PID {proc.ProcessId}) ===");
            Console.WriteLine();
            Console.WriteLine($"  Methods JIT'd:       {proc.TotalMethodsJitted}");
            Console.WriteLine($"    Foreground:        {proc.ForegroundCount}");
            Console.WriteLine($"    Background:        {proc.BackgroundCount}");
            Console.WriteLine();
            Console.WriteLine($"  Total JIT CPU Time:  {proc.TotalJitCpuTimeMSec:F1} ms");
            Console.WriteLine($"  Total IL Size:       {proc.TotalILSize:N0} bytes");
            Console.WriteLine($"  Total Native Size:   {proc.TotalNativeSize:N0} bytes");
            Console.WriteLine($"  IL to Native Ratio:  {(proc.TotalILSize > 0 ? (double)proc.TotalNativeSize / proc.TotalILSize : 0):F2}x");
            Console.WriteLine();
            Console.WriteLine($"  Inlining Successes:  {proc.InliningSuccesses}");
            Console.WriteLine($"  Inlining Failures:   {proc.InliningFailures}");
            Console.WriteLine();
        }
    }

    private static void OutputJson(List<JitProcessStats> processes)
    {
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        Console.WriteLine(JsonSerializer.Serialize(processes, options));
    }

    private class JitProcessStats
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public long TotalMethodsJitted { get; set; }
        public double TotalJitCpuTimeMSec { get; set; }
        public long TotalILSize { get; set; }
        public long TotalNativeSize { get; set; }
        public long ForegroundCount { get; set; }
        public long BackgroundCount { get; set; }
        public int InliningSuccesses { get; set; }
        public int InliningFailures { get; set; }
    }
}
