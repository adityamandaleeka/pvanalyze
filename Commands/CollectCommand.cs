using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace PVAnalyze.Commands;

public static class CollectCommand
{
    private static readonly TimeSpan SessionStopTimeout = TimeSpan.FromSeconds(5);

    public static Command Create()
    {
        var outputOption = new Option<FileInfo>("--output", "-o")
        {
            DefaultValueFactory = _ => new FileInfo("trace.nettrace"),
            Description = "Output .nettrace file"
        };
        var processIdOption = new Option<int?>("--process-id", "-p")
        {
            Description = "Process ID to attach to"
        };
        var profileOption = new Option<string>("--profile")
        {
            DefaultValueFactory = _ => "cpu",
            Description = "Built-in profile: cpu, gc-verbose, or none"
        };
        profileOption.AcceptOnlyFromAmong("cpu", "gc-verbose", "none");
        var providersOption = new Option<string?>("--providers")
        {
            Description = "Providers separated by ';' as Name:Keywords:Level[:key=value,key=value]"
        };
        var durationOption = new Option<double>("--duration-seconds")
        {
            DefaultValueFactory = _ => 30,
            Description = "Collection duration in seconds"
        };
        var delayOption = new Option<double>("--delay-seconds")
        {
            Description = "Delay before attaching, for launched processes"
        };
        var bufferSizeOption = new Option<int>("--buffer-size-mb")
        {
            DefaultValueFactory = _ => 256,
            Description = "EventPipe circular buffer size in MB"
        };
        var startupTimeoutOption = new Option<double>("--startup-timeout-seconds")
        {
            DefaultValueFactory = _ => 10,
            Description = "Maximum time to wait for the target diagnostics server"
        };
        var rundownOption = new Option<bool>("--rundown")
        {
            DefaultValueFactory = _ => true,
            Description = "Request runtime rundown events"
        };
        var commandArgument = new Argument<string[]>("command")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Command to launch, specified after '--'"
        };

        var command = new Command("collect", "Collect an EventPipe trace natively")
        {
            outputOption,
            processIdOption,
            profileOption,
            providersOption,
            durationOption,
            delayOption,
            bufferSizeOption,
            startupTimeoutOption,
            rundownOption,
            commandArgument
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            await Execute(
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(processIdOption),
                parseResult.GetValue(profileOption)!,
                parseResult.GetValue(providersOption),
                parseResult.GetValue(durationOption),
                parseResult.GetValue(delayOption),
                parseResult.GetValue(bufferSizeOption),
                parseResult.GetValue(startupTimeoutOption),
                parseResult.GetValue(rundownOption),
                parseResult.GetValue(commandArgument)!,
                cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task Execute(
        FileInfo output,
        int? processId,
        string profile,
        string? providerSpecs,
        double durationSeconds,
        double delaySeconds,
        int bufferSizeMb,
        double startupTimeoutSeconds,
        bool rundown,
        string[] command,
        CancellationToken cancellationToken)
    {
        if (processId is not null && command.Length > 0)
        {
            throw new ArgumentException("Specify either --process-id or a command, not both.");
        }

        if (processId is null && command.Length == 0)
        {
            throw new ArgumentException("Specify --process-id or a command after '--'.");
        }

        if (bufferSizeMb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSizeMb), "Buffer size must be positive.");
        }

        var duration = GetTimeSpan(durationSeconds, nameof(durationSeconds), allowZero: false);
        var delay = GetTimeSpan(delaySeconds, nameof(delaySeconds), allowZero: true);
        var startupTimeout = GetTimeSpan(startupTimeoutSeconds, nameof(startupTimeoutSeconds), allowZero: false);

        var providers = CreateProviders(profile, providerSpecs);
        if (providers.Count == 0)
        {
            throw new ArgumentException("The 'none' profile requires at least one provider.");
        }

        output.Directory?.Create();
        await using var outputStream = new FileStream(
            output.FullName,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);

        Process? launchedProcess = null;
        try
        {
            if (command.Length > 0)
            {
                var startInfo = new ProcessStartInfo(command[0])
                {
                    UseShellExecute = false
                };

                foreach (var argument in command.Skip(1))
                {
                    startInfo.ArgumentList.Add(argument);
                }

                launchedProcess = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Failed to launch '{command[0]}'.");
                processId = launchedProcess.Id;
                Console.WriteLine($"Launched PID {processId}: {string.Join(' ', command)}");
            }

            if (delay > TimeSpan.Zero)
            {
                var delayTask = Task.Delay(delay, cancellationToken);
                if (launchedProcess is null)
                {
                    await delayTask.ConfigureAwait(false);
                }
                else
                {
                    var earlyExitTask = launchedProcess.WaitForExitAsync(cancellationToken);
                    if (await Task.WhenAny(delayTask, earlyExitTask).ConfigureAwait(false) == earlyExitTask)
                    {
                        await earlyExitTask.ConfigureAwait(false);
                        throw new InvalidOperationException(
                            $"Launched process exited with code {launchedProcess.ExitCode} before collection began.");
                    }

                    await delayTask.ConfigureAwait(false);
                }
            }

            if (launchedProcess?.HasExited is true)
            {
                throw new InvalidOperationException(
                    $"Launched process exited with code {launchedProcess.ExitCode} before collection began.");
            }

            Console.WriteLine($"Collecting PID {processId} for {durationSeconds:F1}s to {output.FullName}");

            var client = new DiagnosticsClient(processId!.Value);
            using var session = await StartSessionAsync(
                client,
                providers,
                rundown,
                bufferSizeMb,
                startupTimeout,
                launchedProcess,
                cancellationToken).ConfigureAwait(false);

            var copyTask = session.EventStream.CopyToAsync(outputStream, cancellationToken);
            var durationTask = Task.Delay(duration, cancellationToken);
            var processExitTask = launchedProcess?.WaitForExitAsync(cancellationToken);
            try
            {
                if (processExitTask is null)
                {
                    await Task.WhenAny(copyTask, durationTask).ConfigureAwait(false);
                }
                else
                {
                    await Task.WhenAny(copyTask, durationTask, processExitTask).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    using var stopCancellation = new CancellationTokenSource(SessionStopTimeout);
                    await session.StopAsync(stopCancellation.Token).ConfigureAwait(false);
                }
                catch (ServerNotAvailableException) when (
                    copyTask.IsCompleted
                    || processExitTask?.IsCompleted is true
                    || launchedProcess?.HasExited is true)
                {
                    // The target closed the diagnostic server while ending normally.
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException(
                        $"The EventPipe session did not stop within {SessionStopTimeout.TotalSeconds:F1}s.");
                }
            }

            await copyTask.ConfigureAwait(false);
            await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Trace completed: {output.FullName}");

            if (processExitTask is not null)
            {
                try
                {
                    await processExitTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The capture duration can intentionally be shorter than the launched process lifetime.
                }
            }

            if (launchedProcess?.HasExited is true && launchedProcess.ExitCode != 0)
            {
                throw new InvalidOperationException($"Launched process exited with code {launchedProcess.ExitCode}.");
            }
        }
        catch
        {
            if (launchedProcess is { HasExited: false })
            {
                launchedProcess.Kill(entireProcessTree: true);
                await launchedProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            launchedProcess?.Dispose();
        }
    }

    private static async Task<EventPipeSession> StartSessionAsync(
        DiagnosticsClient client,
        IReadOnlyCollection<EventPipeProvider> providers,
        bool rundown,
        int bufferSizeMb,
        TimeSpan startupTimeout,
        Process? launchedProcess,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (launchedProcess?.HasExited is true)
            {
                throw new InvalidOperationException(
                    $"Launched process exited with code {launchedProcess.ExitCode} before collection began.");
            }

            var remaining = startupTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"The target diagnostics server did not become available within {startupTimeout.TotalSeconds:F1}s.");
            }

            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(remaining);
            try
            {
                return await client.StartEventPipeSessionAsync(
                    providers,
                    rundown,
                    bufferSizeMb,
                    attemptCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The target diagnostics server did not become available within {startupTimeout.TotalSeconds:F1}s.");
            }
            catch (ServerNotAvailableException exception)
            {
                remaining = startupTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException(
                        $"The target diagnostics server did not become available within {startupTimeout.TotalSeconds:F1}s.",
                        exception);
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(100, remaining.TotalMilliseconds)),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static List<EventPipeProvider> CreateProviders(string profile, string? providerSpecs)
    {
        var result = new List<EventPipeProvider>();
        switch (profile)
        {
            case "cpu":
                result.Add(new EventPipeProvider(
                    "Microsoft-DotNETCore-SampleProfiler",
                    EventLevel.Informational));
                break;
            case "gc-verbose":
                result.Add(new EventPipeProvider(
                    "Microsoft-Windows-DotNETRuntime",
                    EventLevel.Verbose,
                    keywords:
                        (long)ClrTraceEventParser.Keywords.GC
                        | (long)ClrTraceEventParser.Keywords.GCHandle
                        | (long)ClrTraceEventParser.Keywords.Exception));
                break;
            case "none":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(providerSpecs))
        {
            return result;
        }

        foreach (var spec in providerSpecs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = spec.Split(':', 4, StringSplitOptions.TrimEntries);
            if (parts.Length is < 3 or > 4
                || !TryParseInt64(parts[1], out var keywords)
                || !int.TryParse(parts[2], out var level)
                || !Enum.IsDefined(typeof(EventLevel), level))
            {
                throw new ArgumentException(
                    $"Invalid provider '{spec}'. Expected Name:Keywords:Level[:key=value,key=value].",
                    nameof(providerSpecs));
            }

            IDictionary<string, string>? arguments = null;
            if (parts.Length == 4)
            {
                arguments = ParseProviderArguments(parts[3], spec, providerSpecs);
            }

            result.Add(new EventPipeProvider(parts[0], (EventLevel)level, keywords, arguments));
        }

        return result;
    }

    private static Dictionary<string, string> ParseProviderArguments(
        string value,
        string spec,
        string providerSpecs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = argument.IndexOf('=');
            if (separator <= 0 || separator == argument.Length - 1)
            {
                throw new ArgumentException(
                    $"Invalid provider argument '{argument}' in '{spec}'. Expected key=value.",
                    nameof(providerSpecs));
            }

            var key = argument[..separator].Trim();
            var argumentValue = argument[(separator + 1)..].Trim();
            if (!result.TryAdd(key, argumentValue))
            {
                throw new ArgumentException(
                    $"Duplicate provider argument '{key}' in '{spec}'.",
                    nameof(providerSpecs));
            }
        }

        if (result.Count == 0)
        {
            throw new ArgumentException(
                $"Provider '{spec}' has an empty argument list.",
                nameof(providerSpecs));
        }

        return result;
    }

    private static TimeSpan GetTimeSpan(double seconds, string parameterName, bool allowZero)
    {
        var minimumIsValid = allowZero ? seconds >= 0 : seconds > 0;
        var maxDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (!double.IsFinite(seconds) || !minimumIsValid || seconds > maxDelay.TotalSeconds)
        {
            var range = allowZero ? "non-negative" : "positive";
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The value must be finite, {range}, and no greater than {maxDelay.TotalSeconds:F0} seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static bool TryParseInt64(string value, out long result)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(
                value.AsSpan(2),
                System.Globalization.NumberStyles.HexNumber,
                provider: null,
                out result);
        }

        return long.TryParse(value, out result);
    }
}
