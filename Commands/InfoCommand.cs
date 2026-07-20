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
            Description = "Path to the .nettrace file to analyze"
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

            long rpcPhaseEventCount = 0;
            foreach (var traceEvent in traceLog.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (RpcPhaseProjector.IsRpcPhaseEvent(traceEvent))
                {
                    rpcPhaseEventCount++;
                }
            }

            if (rpcPhaseEventCount > 0)
            {
                Console.WriteLine($"RPC Phase Events:{rpcPhaseEventCount,15:N0}");
            }

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
}
