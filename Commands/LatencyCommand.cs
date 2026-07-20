using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json.Serialization;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RpcLatencyResponse))]
internal partial class LatencyJsonContext : JsonSerializerContext { }

public static class LatencyCommand
{
    public static Command Create()
    {
        var traceFileArgument = new Argument<FileInfo>("trace-file")
        {
            Description = "Path to a .nettrace or .etlx file"
        };
        var providerOption = new Option<string>("--provider")
        {
            DefaultValueFactory = _ => RpcPhaseProjector.DefaultProviderName,
            Description = "RPC phase EventSource provider name"
        };
        var pidOption = new Option<int?>("--pid") { Description = "Select calls involving this process ID" };
        var processOption = new Option<string?>("--process") { Description = "Select calls involving a matching process name" };
        var processRoleOption = new Option<string?>("--process-role") { Description = "Select calls by originating process role (driver/origin or target/callee)" };
        var originSiloOption = new Option<string?>("--origin-silo") { Description = "Origin silo as port:generation" };
        var traceIdOption = new Option<string?>("--trace-id") { Description = "Exact 32-hex-character W3C trace ID" };
        var correlationIdOption = new Option<string?>("--correlation-id") { Description = "Correlation ID in decimal or 0x-prefixed hexadecimal" };
        var fromOption = new Option<double?>("--from") { Description = "Start time in milliseconds" };
        var toOption = new Option<double?>("--to") { Description = "End time in milliseconds" };
        var measurementWindowOption = new Option<bool>("--measurement-window") { Description = "Use captured MeasurementStart/MeasurementStop markers" };
        var successfulOnlyOption = new Option<bool>("--successful-only")
        {
            DefaultValueFactory = _ => true,
            Description = "Exclude failed, rejected, timed-out, and cancelled calls"
        };
        var includeIncompleteOption = new Option<bool>("--include-incomplete") { Description = "Include incomplete calls in duration distributions" };
        var minCompletenessOption = new Option<double>("--min-completeness")
        {
            DefaultValueFactory = _ => 0,
            Description = "Minimum call and phase completeness ratio (0 through 1)"
        };
        var timelineOption = new Option<bool>("--timeline") { Description = "Print an ordered timeline when one call is selected" };
        var queuesOption = new Option<bool>("--queues") { Description = "Include queue residency and depth analysis" };
        var queueOption = new Option<string?>("--queue") { Description = "Restrict queue output by name or resource kind" };
        var withCpuOption = new Option<bool>("--with-cpu") { Description = "Add sampled CPU attribution metadata when available" };
        var formatOption = new Option<OutputFormat>("--format")
        {
            DefaultValueFactory = _ => OutputFormat.Text,
            Description = "Output format"
        };
        var outputOption = new Option<FileInfo?>("--output", "-o") { Description = "Output file (default: stdout)" };

        var command = new Command("phases", "Analyze correlated Orleans RPC phase and queue latency")
        {
            traceFileArgument,
            providerOption,
            pidOption,
            processOption,
            processRoleOption,
            originSiloOption,
            traceIdOption,
            correlationIdOption,
            fromOption,
            toOption,
            measurementWindowOption,
            successfulOnlyOption,
            includeIncompleteOption,
            minCompletenessOption,
            timelineOption,
            queuesOption,
            queueOption,
            withCpuOption,
            formatOption,
            outputOption
        };
        command.Aliases.Add("latency");
        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            await Execute(
                parseResult.GetValue(traceFileArgument)!,
                parseResult.GetValue(providerOption)!,
                parseResult.GetValue(pidOption),
                parseResult.GetValue(processOption),
                parseResult.GetValue(processRoleOption),
                parseResult.GetValue(originSiloOption),
                parseResult.GetValue(traceIdOption),
                parseResult.GetValue(correlationIdOption),
                parseResult.GetValue(fromOption),
                parseResult.GetValue(toOption),
                parseResult.GetValue(measurementWindowOption),
                parseResult.GetValue(successfulOnlyOption),
                parseResult.GetValue(includeIncompleteOption),
                parseResult.GetValue(minCompletenessOption),
                parseResult.GetValue(timelineOption),
                parseResult.GetValue(queuesOption),
                parseResult.GetValue(queueOption),
                parseResult.GetValue(withCpuOption),
                parseResult.GetValue(formatOption),
                parseResult.GetValue(outputOption),
                cancellationToken).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task Execute(
        FileInfo traceFile,
        string provider,
        int? processId,
        string? processName,
        string? processRole,
        string? originSiloText,
        string? traceId,
        string? correlationIdText,
        double? fromMs,
        double? toMs,
        bool measurementWindow,
        bool successfulOnly,
        bool includeIncomplete,
        double minCompleteness,
        bool timeline,
        bool queues,
        string? queue,
        bool withCpu,
        OutputFormat format,
        FileInfo? output,
        CancellationToken cancellationToken)
    {
        if (!traceFile.Exists)
        {
            Console.Error.WriteLine($"Error: File not found: {traceFile.FullName}");
            return;
        }
        if (minCompleteness is < 0 or > 1 || !double.IsFinite(minCompleteness))
        {
            throw new ArgumentOutOfRangeException(nameof(minCompleteness), "Completeness must be between 0 and 1.");
        }
        if (!TryParseSilo(originSiloText, out var originSilo))
        {
            throw new ArgumentException("Origin silo must use port:generation.", nameof(originSiloText));
        }

        ulong? traceHigh = null;
        ulong? traceLow = null;
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            if (!RpcPhaseProjector.TryParseTraceId(traceId, out var high, out var low))
            {
                throw new ArgumentException("Trace ID must contain exactly 32 hexadecimal characters.", nameof(traceId));
            }
            traceHigh = high;
            traceLow = low;
        }

        long? correlationId = null;
        if (!string.IsNullOrWhiteSpace(correlationIdText))
        {
            correlationId = ParseCorrelationId(correlationIdText);
        }

        var etlxPath = string.Equals(traceFile.Extension, ".etlx", StringComparison.OrdinalIgnoreCase)
            ? traceFile.FullName
            : await EtlxCache.GetOrCreateEtlxAsync(traceFile.FullName, cancellationToken).ConfigureAwait(false);
        using var traceLog = new Etlx.TraceLog(etlxPath);
        var processNames = traceLog.Processes
            .GroupBy(static process => process.ProcessID)
            .ToDictionary(static group => group.Key, static group => group.First().Name);
        var projected = new List<RpcPhaseEvent>();
        var processRoles = new Dictionary<int, string>();
        var measurementStarts = new List<(double TimestampMs, int ProcessId, string? Role)>();
        var measurementStops = new List<(double TimestampMs, int ProcessId, string? Role)>();
        long malformed = 0;

        foreach (var traceEvent in traceLog.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadBenchmarkPhase(traceEvent, provider, out var benchmarkPhase, out var benchmarkRole))
            {
                if (benchmarkRole is not null)
                {
                    processRoles[traceEvent.ProcessID] = benchmarkRole;
                }
                if (measurementWindow && benchmarkPhase == RpcBenchmarkPhase.MeasurementStart)
                {
                    measurementStarts.Add((traceEvent.TimeStampRelativeMSec, traceEvent.ProcessID, benchmarkRole));
                }
                else if (measurementWindow && benchmarkPhase == RpcBenchmarkPhase.MeasurementStop)
                {
                    measurementStops.Add((traceEvent.TimeStampRelativeMSec, traceEvent.ProcessID, benchmarkRole));
                }
            }
            else if (measurementWindow)
            {
                if (IsMarker(traceEvent.EventName, "MeasurementStart"))
                    measurementStarts.Add((traceEvent.TimeStampRelativeMSec, traceEvent.ProcessID, null));
                else if (IsMarker(traceEvent.EventName, "MeasurementStop"))
                    measurementStops.Add((traceEvent.TimeStampRelativeMSec, traceEvent.ProcessID, null));
            }

            if (!RpcPhaseProjector.IsRpcPhaseEvent(traceEvent, provider))
            {
                continue;
            }
            processNames.TryGetValue(traceEvent.ProcessID, out var eventProcessName);
            if (RpcPhaseProjector.TryProject(traceEvent, provider, eventProcessName ?? "Unknown", out var phaseEvent))
            {
                projected.Add(phaseEvent!);
            }
            else
            {
                malformed++;
            }
        }

        for (var index = 0; index < projected.Count; index++)
        {
            if (processRoles.TryGetValue(projected[index].ProcessId, out var role))
            {
                projected[index] = projected[index] with { RecordedProcessRole = role };
            }
        }

        var windowSource = fromMs.HasValue || toMs.HasValue ? "explicit" : "trace";
        if (measurementWindow)
        {
            if (fromMs.HasValue || toMs.HasValue)
            {
                throw new ArgumentException("--measurement-window cannot be combined with --from or --to.");
            }
            var driverProcessIds = measurementStarts
                .Concat(measurementStops)
                .Where(static marker => marker.Role == "driver")
                .Select(static marker => marker.ProcessId)
                .Distinct()
                .ToList();
            if (driverProcessIds.Count > 1)
            {
                throw new InvalidDataException(
                    $"Measurement markers identify multiple driver processes ({string.Join(", ", driverProcessIds)}).");
            }

            var selectedStarts = driverProcessIds.Count == 1
                ? measurementStarts.Where(marker => marker.ProcessId == driverProcessIds[0]).ToList()
                : measurementStarts;
            var selectedStops = driverProcessIds.Count == 1
                ? measurementStops.Where(marker => marker.ProcessId == driverProcessIds[0]).ToList()
                : measurementStops;
            if (selectedStarts.Count == 0 || selectedStops.Count == 0)
            {
                throw new InvalidDataException("MeasurementStart and MeasurementStop markers were not found.");
            }

            fromMs = selectedStarts.Min(static marker => marker.TimestampMs);
            toMs = selectedStops.Max(static marker => marker.TimestampMs);
            if (toMs < fromMs)
            {
                throw new InvalidDataException("MeasurementStop precedes MeasurementStart.");
            }
            windowSource = "measurement-markers";
        }

        var options = new RpcPhaseAnalysisOptions(
            processId,
            processName,
            processRole,
            originSilo,
            traceHigh,
            traceLow,
            correlationId,
            fromMs,
            toMs,
            successfulOnly,
            includeIncomplete,
            minCompleteness,
            timeline,
            queues || !string.IsNullOrWhiteSpace(queue),
            queue,
            withCpu,
            provider,
            windowSource);
        var response = TraceAnalyzer.AnalyzeRpcPhases(
            projected,
            options,
            traceLog.SessionDuration.TotalMilliseconds,
            traceLog.EventsLost);
        if (malformed > 0)
        {
            response.Warnings.Add($"{malformed:N0} malformed Phase event(s) could not be projected.");
        }

        if (format == OutputFormat.Json)
        {
            if (output is null)
            {
                await JsonOutput.WriteAsync(response, LatencyJsonContext.Default.RpcLatencyResponse, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                output.Directory?.Create();
                await JsonOutput.WriteToFileAsync(response, output.FullName, LatencyJsonContext.Default.RpcLatencyResponse, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var text = FormatText(response);
        if (output is null)
        {
            Console.Write(text);
        }
        else
        {
            output.Directory?.Create();
            await File.WriteAllTextAsync(output.FullName, text, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FormatText(RpcLatencyResponse response)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine(
            $"Provider: {response.Provider.Name}  Events: {response.Provider.EventCount:N0}  "
            + $"Lost: {response.Provider.LostEvents:N0}  Exact: {response.Provider.ExactCallCount:N0}  "
            + $"Sampled: {response.Provider.DeterministicSampleCallCount:N0}");
        writer.WriteLine($"Window: {response.Window.FromMs:F3}ms - {response.Window.ToMs:F3}ms ({response.Window.Source})");
        writer.WriteLine($"Calls: {response.SampledCallCount:N0} sampled, ~{response.EstimatedSourceCallCount:N0} source  Completeness: {response.Completeness:P1}  Sample rate: {response.SamplingRate?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
        writer.WriteLine();
        writer.WriteLine($"{"Phase",-38} {"Count",8} {"Complete",9} {"Mean",10} {"P50",10} {"P90",10} {"P99",10} {"P99.9",10} {"Max",10} {"StdDev",10} {"MAD",10}");
        writer.WriteLine(new string('-', 150));
        foreach (var phase in response.Phases)
        {
            writer.WriteLine($"{phase.Name,-38} {phase.Duration.Count,8:N0} {phase.Completeness,8:P1} {Us(phase.Duration.MeanUs),10} {Us(phase.Duration.P50Us),10} {Us(phase.Duration.P90Us),10} {Us(phase.Duration.P99Us),10} {Us(phase.Duration.P999Us),10} {Us(phase.Duration.MaxUs),10} {Us(phase.Duration.StandardDeviationUs),10} {Us(phase.Duration.MedianAbsoluteDeviationUs),10}");
        }
        var diagnosticPhases = response.Phases.Where(static phase =>
            phase.Diagnostics.MissingStart != 0
            || phase.Diagnostics.MissingStop != 0
            || phase.Diagnostics.Duplicate != 0
            || phase.Diagnostics.OutOfOrder != 0
            || phase.Diagnostics.Retry != 0
            || phase.Diagnostics.Forwarded != 0
            || phase.Diagnostics.CrossProcess != 0).ToList();
        if (diagnosticPhases.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Phase diagnostics:");
            foreach (var phase in diagnosticPhases)
            {
                writer.WriteLine(
                    $"  {phase.Name}: missing-start={phase.Diagnostics.MissingStart}, "
                    + $"missing-stop={phase.Diagnostics.MissingStop}, duplicate={phase.Diagnostics.Duplicate}, "
                    + $"out-of-order={phase.Diagnostics.OutOfOrder}, retries={phase.Diagnostics.Retry}, "
                    + $"forwarded={phase.Diagnostics.Forwarded}, cross-process={phase.Diagnostics.CrossProcess}");
            }
        }

        if (response.Queues.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine(
                $"Per-call queue total: mean {Us(response.PerCallQueueTime.MeanUs)}, "
                + $"p50 {Us(response.PerCallQueueTime.P50Us)}, "
                + $"p99 {Us(response.PerCallQueueTime.P99Us)}, "
                + $"max {Us(response.PerCallQueueTime.MaxUs)}");
            writer.WriteLine($"{"Queue",-28} {"Count",8} {"Mean",10} {"P50",10} {"P90",10} {"P99",10} {"P99.9",10} {"Max",10} {"MeanDepth",12} {"L=lambda*W",12}");
            writer.WriteLine(new string('-', 130));
            foreach (var queue in response.Queues)
            {
                var depth = queue.ContextualDepth ? "contextual" : queue.MeanDepth?.ToString("F2", CultureInfo.InvariantCulture) ?? "-";
                writer.WriteLine($"{queue.Name,-28} {queue.CompletedWaits,8:N0} {Us(queue.Wait.MeanUs),10} {Us(queue.Wait.P50Us),10} {Us(queue.Wait.P90Us),10} {Us(queue.Wait.P99Us),10} {Us(queue.Wait.P999Us),10} {Us(queue.Wait.MaxUs),10} {depth,12} {queue.InferredMeanQueueLength,12:F3}");
                if (queue.WaitByDepth.Count > 0)
                {
                    writer.WriteLine("  by depth: " + string.Join(", ", queue.WaitByDepth.Select(item => $"{item.Value}:{item.Count} @ {Us(item.Wait.MeanUs)}")));
                }
                if (queue.WaitByBatchIndex.Count > 0)
                {
                    writer.WriteLine("  by batch index: " + string.Join(", ", queue.WaitByBatchIndex.Select(item => $"{item.Value}:{item.Count} @ {Us(item.Wait.MeanUs)}")));
                }
                if (queue.WaitByBatchSize.Count > 0)
                {
                    writer.WriteLine("  by batch size: " + string.Join(", ", queue.WaitByBatchSize.Select(item => $"{item.Value}:{item.Count} @ {Us(item.Wait.MeanUs)}")));
                }
            }
        }

        if (response.Timeline is { Count: > 0 } timeline)
        {
            var call = response.Calls[0];
            writer.WriteLine();
            writer.WriteLine($"Trace {call.TraceId}  Correlation {call.CorrelationId}");
            foreach (var item in timeline)
            {
                var queue = item.QueueDepth.HasValue ? $" depth={item.QueueDepth}" : "";
                var batch = item.BatchSize.HasValue ? $" batch={item.BatchSize} index={item.BatchIndex}" : "";
                var resource = item.ResourceId != 0 ? $" resource={item.ResourceKind}/{item.ResourceId}" : "";
                var attempt = item.RetryCount != 0 || item.ForwardCount != 0
                    ? $" retry={item.RetryCount} forward={item.ForwardCount}" : "";
                var operation = item.OperationDurationUs.HasValue ? $" operation={Us(item.OperationDurationUs.Value)}" : "";
                writer.WriteLine($"{item.CumulativeUs,+11:F3}us  +{item.DeltaUs,9:F3}us  {item.ProcessRole,-6} P{item.ProcessId} T{item.ThreadId:D2} CPU{item.ProcessorNumber:D2} silo={item.LocalSilo} {item.Direction,-8} {item.Phase,-26}{resource}{queue}{batch}{attempt}{operation}");
            }
        }

        if (response.Warnings.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Warnings:");
            foreach (var warning in response.Warnings)
            {
                writer.WriteLine($"  - {warning}");
            }
        }
        return writer.ToString();
    }

    private static string Us(double value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000:F2}s",
        >= 1_000 => $"{value / 1_000:F2}ms",
        _ => $"{value:F2}us"
    };

    private static bool IsMarker(string eventName, string marker) =>
        eventName.Equals(marker, StringComparison.OrdinalIgnoreCase)
        || eventName.EndsWith($"/{marker}", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadBenchmarkPhase(
        Microsoft.Diagnostics.Tracing.TraceEvent traceEvent,
        string provider,
        out RpcBenchmarkPhase phase,
        out string? role)
    {
        phase = RpcBenchmarkPhase.Unknown;
        role = null;
        if (!traceEvent.ProviderName.Equals(provider, StringComparison.OrdinalIgnoreCase)
            || !traceEvent.EventName.Equals("BenchmarkPhase", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            phase = (RpcBenchmarkPhase)Convert.ToByte(
                traceEvent.PayloadByName("phase"),
                CultureInfo.InvariantCulture);
            var processRole = (RpcProcessRole)Convert.ToByte(
                traceEvent.PayloadByName("processRole"),
                CultureInfo.InvariantCulture);
            role = processRole switch
            {
                RpcProcessRole.Driver => "driver",
                RpcProcessRole.Target => "target",
                _ => null
            };
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseSilo(string? value, out RpcSiloIdentity? silo)
    {
        silo = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        var parts = value.Split([':', '/'], StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation))
        {
            return false;
        }
        silo = new RpcSiloIdentity(port, generation);
        return true;
    }

    private static long ParseCorrelationId(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unsigned))
        {
            return unchecked((long)unsigned);
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
        {
            return signed;
        }
        throw new ArgumentException("Correlation ID must be decimal or 0x-prefixed hexadecimal.", nameof(value));
    }
}
