using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

public static class InfoCommand
{
    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file")
        {
            Description = "Path to a .nettrace, .etl, .etl.zip, or .etlx file"
        };

        var command = new Command("info", "Display basic trace information")
        {
            traceFileArg
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            var traceFile = parseResult.GetValue(traceFileArg)!;
            await Execute(traceFile, cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(FileInfo traceFile, CancellationToken cancellationToken)
    {
        if (!traceFile.Exists)
        {
            Console.Error.WriteLine($"Error: File not found: {traceFile.FullName}");
            return;
        }

        Console.WriteLine($"Analyzing: {traceFile.Name}");
        Console.WriteLine();

        try
        {
            string etlxPath = await EtlxCache.GetOrCreateEtlxAsync(traceFile.FullName, cancellationToken).ConfigureAwait(false);
            
            using var traceLog = new TraceLog(etlxPath);
            
            Console.WriteLine("=== Trace Information ===");
            Console.WriteLine($"Duration:        {traceLog.SessionDuration.TotalSeconds:F2} seconds");
            Console.WriteLine($"Start Time:      {traceLog.SessionStartTime:yyyy-MM-dd HH:mm:ss.fff}");
            Console.WriteLine($"End Time:        {traceLog.SessionEndTime:yyyy-MM-dd HH:mm:ss.fff}");
            Console.WriteLine($"Event Count:     {traceLog.EventCount:N0}");
            Console.WriteLine($"Lost Events:     {traceLog.EventsLost:N0}");
            Console.WriteLine($"Pointer Size:    {traceLog.PointerSize * 8}-bit");
            Console.WriteLine($"CPU Count:       {traceLog.NumberOfProcessors}");
            Console.WriteLine();

            var capabilities = TraceCapabilityDetector.Detect(traceLog);
            Console.WriteLine("=== Available Analyses ===");
            PrintCapability("CPU stacks", capabilities.SupportsCpuStacks,
                $"{capabilities.CpuSampleCount:N0} samples", "cpustacks --stack-source cpu");
            PrintCapability("Thread time", capabilities.SupportsThreadTime,
                $"{capabilities.ContextSwitchCount:N0} context switches", "stacks --stack-source threadtime");
            PrintCapability("CPU activities", capabilities.SupportsCpuActivityStacks,
                $"{capabilities.CpuSampleCount:N0} CPU, {capabilities.StartStopEventCount:N0} Start/Stop",
                "stacks --stack-source activity-cpu");
            PrintCapability("Full activities", capabilities.SupportsThreadTimeActivityStacks,
                $"{capabilities.ContextSwitchCount:N0} ctx, {capabilities.StartStopEventCount:N0} Start/Stop",
                "stacks --stack-source activity-threadtime");
            PrintCapability("Hardware counters", capabilities.HardwareCounterSampleCount > 0,
                $"{capabilities.HardwareCounterSampleCount:N0} PMC samples", "events --type PMCSample");
            PrintCapability("GC", capabilities.GcEventCount > 0,
                $"{capabilities.GcEventCount:N0} GC lifecycle events", "gcstats");
            PrintCapability("Allocations", capabilities.AllocationEventCount > 0,
                $"{capabilities.AllocationEventCount:N0} allocation events", "alloc");
            PrintCapability("Exceptions", capabilities.ExceptionEventCount > 0,
                $"{capabilities.ExceptionEventCount:N0} exception events", "exceptions");
            PrintCapability("JIT", capabilities.JitEventCount > 0,
                $"{capabilities.JitEventCount:N0} JIT events", "jitstats");
            Console.WriteLine();

            Console.WriteLine("=== Processes ===");
            foreach (var process in traceLog.Processes.OrderByDescending(p => p.CPUMSec))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.CPUMSec > 0 || process.Name != "Unknown")
                {
                    Console.WriteLine($"  PID {process.ProcessID,6}: {process.Name,-30} CPU: {process.CPUMSec:F1} ms");
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

    private static void PrintCapability(string analysis, bool available, string evidence, string command)
    {
        string status = available ? "yes" : "no";
        Console.WriteLine($"  {analysis,-18} {status,-3}  {evidence,-28} {command}");
    }
}
