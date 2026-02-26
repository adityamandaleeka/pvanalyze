using System.CommandLine;
using PVAnalyze.Commands;

namespace PVAnalyze;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("pvanalyze - Cross-platform .NET trace analyzer")
        {
            InfoCommand.Create(),
            GcStatsCommand.Create(),
            JitStatsCommand.Create(),
            CpuStacksCommand.Create(),
            EventsCommand.Create(),
            ExceptionsCommand.Create(),
            CallTreeCommand.Create(),
            AllocCommand.Create(),
            TimelineCommand.Create(),
            SnapshotCommand.Create(),
            ServeCommand.Create(),
        };

        return await rootCommand.InvokeAsync(args);
    }
}
