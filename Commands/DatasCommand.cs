using System.CommandLine;
using System.Text.Json;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

public static class DatasCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file", "Path to the .nettrace or .etl file to analyze");
        var formatOption = new Option<OutputFormat>("--format", () => OutputFormat.Text, "Output format");
        var processOption = new Option<string?>("--process", "Filter by process name");
        var samplesOption = new Option<bool>("--samples", "Show per-GC DATAS samples");
        var tuningOption = new Option<bool>("--tuning", "Show heap count tuning decisions");
        var gen2Option = new Option<bool>("--gen2", "Show gen2 full GC tuning events");

        var command = new Command("datas", "Display DATAS (Dynamic Adaptation) statistics from a trace")
        {
            traceFileArg,
            formatOption,
            processOption,
            samplesOption,
            tuningOption,
            gen2Option
        };

        command.SetHandler(Execute, traceFileArg, formatOption, processOption, samplesOption, tuningOption, gen2Option);
        return command;
    }

    private static void Execute(FileInfo traceFile, OutputFormat format, string? processFilter,
        bool showSamples, bool showTuning, bool showGen2)
    {
        if (!traceFile.Exists)
        {
            Console.Error.WriteLine($"Error: File not found: {traceFile.FullName}");
            return;
        }

        try
        {
            string etlxPath = EtlxCache.GetOrCreateEtlx(traceFile.FullName);
            using var traceLog = new Etlx.TraceLog(etlxPath);

            var results = TraceAnalyzer.GetDatasStats(traceLog, processFilter);

            if (results.Count == 0)
            {
                Console.Error.WriteLine("No DATAS events found in trace.");
                Console.Error.WriteLine("DATAS events require .NET 8+ with GCDynamicAdaptation enabled and GC ETW events collected.");
                return;
            }

            if (format == OutputFormat.Json)
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                Console.WriteLine(JsonSerializer.Serialize(results, options));
                return;
            }

            // If no specific view requested, show overview + heap count timeline
            bool showAll = !showSamples && !showTuning && !showGen2;

            foreach (var proc in results)
            {
                Console.WriteLine($"=== DATAS Stats for {proc.ProcessName} (PID {proc.ProcessId}) ===");
                Console.WriteLine();

                if (proc.Overview != null)
                {
                    var o = proc.Overview;
                    Console.WriteLine($"  Tuning Events:       {o.TuningEventCount}");
                    Console.WriteLine($"  Sample Events:       {o.SampleCount}");
                    Console.WriteLine($"  Gen2 Tuning Events:  {o.FullGCTuningCount}");
                    Console.WriteLine($"  Heap Count Range:    {o.MinHeapCount} - {o.MaxHeapCount}");
                    Console.WriteLine($"  Heap Count Changes:  {o.HeapCountChanges}");
                    Console.WriteLine($"  Mean TCP:            {o.MeanThroughputCostPercent:F2}%");
                    Console.WriteLine($"  Max TCP:             {o.MaxThroughputCostPercent:F2}%");
                    Console.WriteLine($"  Mean Gen0 Budget:    {o.MeanGen0BudgetMB:F3} MB/heap");
                    Console.WriteLine($"  Mean SOH Stable:     {o.MeanSohStableSizeMB:F2} MB");
                    Console.WriteLine();
                }

                if (showAll || showTuning)
                {
                    OutputTuningTimeline(proc);
                }

                if (showSamples)
                {
                    OutputSampleTimeline(proc);
                }

                if (showGen2)
                {
                    OutputGen2Tuning(proc);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
        }
    }

    private static void OutputTuningTimeline(DatasResponse proc)
    {
        if (proc.TuningEvents.Count == 0)
        {
            Console.WriteLine("  No heap count tuning events found.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("  --- Heap Count Tuning Timeline ---");
        Console.WriteLine();
        Console.WriteLine($"  {"GC#",8} {"Heaps",6} {"TCP%",8} {"Consider",10} {"Slope",8} {"Since Chg",10} {"Decision",10} {"Reason",8} {"SOH Stable",12}");
        Console.WriteLine($"  {new string('-', 90)}");

        foreach (var t in proc.TuningEvents)
        {
            Console.WriteLine($"  {t.GcIndex,8} {t.NewHeapCount,6} {t.MedianThroughputCostPercent,7:F2}% {t.TcpToConsider,9:F2}% {t.RecordedTcpSlope,8:F3} {t.NumGcsSinceLastChange,10} {t.ChangeDecision,10} {t.AdjReason,8} {t.TotalSohStableSize / 1_000_000.0,11:F2}M");
        }
        Console.WriteLine();
    }

    private static void OutputSampleTimeline(DatasResponse proc)
    {
        if (proc.SampleEvents.Count == 0)
        {
            Console.WriteLine("  No DATAS sample events found.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("  --- DATAS Samples ---");
        Console.WriteLine();
        Console.WriteLine($"  {"GC#",8} {"Elapsed ms",11} {"Pause ms",10} {"TCP%",8} {"SOH MSL us",11} {"UOH MSL us",11} {"Budget/Heap",12} {"SOH Stable",12}");
        Console.WriteLine($"  {new string('-', 96)}");

        foreach (var s in proc.SampleEvents)
        {
            Console.WriteLine($"  {s.GcIndex,8} {s.ElapsedBetweenGcsUs / 1000.0,10:F1} {s.GcPauseTimeUs / 1000.0,9:F2} {s.ThroughputCostPercent,7:F2}% {s.SohMslWaitTimeUs,11} {s.UohMslWaitTimeUs,11} {s.Gen0BudgetPerHeap / 1_000_000.0,11:F3}M {s.TotalSohStableSize / 1_000_000.0,11:F2}M");
        }
        Console.WriteLine();
    }

    private static void OutputGen2Tuning(DatasResponse proc)
    {
        if (proc.FullGCTuningEvents.Count == 0)
        {
            Console.WriteLine("  No gen2 full GC tuning events found.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("  --- Gen2 Full GC Tuning ---");
        Console.WriteLine();
        Console.WriteLine($"  {"GC#",8} {"Heaps",6} {"Gen2 TCP%",10} {"Since Chg",10} {"S0 Age",7} {"S0 GC%",8} {"S1 Age",7} {"S1 GC%",8} {"S2 Age",7} {"S2 GC%",8}");
        Console.WriteLine($"  {new string('-', 92)}");

        foreach (var g in proc.FullGCTuningEvents)
        {
            Console.WriteLine($"  {g.GcIndex,8} {g.NewHeapCount,6} {g.MedianGen2Tcp,9:F2}% {g.NumGen2sSinceLastChange,10} {g.Gen2Sample0Age,7} {g.Gen2Sample0Percent,7:F2}% {g.Gen2Sample1Age,7} {g.Gen2Sample1Percent,7:F2}% {g.Gen2Sample2Age,7} {g.Gen2Sample2Percent,7:F2}%");
        }
        Console.WriteLine();
    }
}
