using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
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
            PmcStatsCommand.Create(),
            EventsCommand.Create(),
            ExceptionsCommand.Create(),
            CallTreeCommand.Create(),
            AllocCommand.Create(),
            TimelineCommand.Create(),
            SnapshotCommand.Create(),
            DatasCommand.Create(),
            CleanCommand.Create(),
        };

        var configuration = new InvocationConfiguration
        {
            ProcessTerminationTimeout = TimeSpan.FromSeconds(5)
        };

        return await rootCommand.Parse(args).InvokeAsync(configuration);
    }
}
