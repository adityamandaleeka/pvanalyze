using System.Globalization;
using Diagnostics.Tracing.StackSources;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Universal.Events;
using Microsoft.Diagnostics.Tracing.Stacks;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze;

public enum StackSourceKind
{
    Cpu,
    ThreadTime,
    Activity,
    ActivityCpu,
    ActivityThreadTime
}

internal static class StackSourceFactory
{
    public static StackSource Create(
        Etlx.TraceLog traceLog,
        StackSourceKind kind,
        double? fromMs,
        double? toMs,
        out StackSourceKind resolvedKind)
    {
        var capabilities = TraceCapabilityDetector.Detect(traceLog);
        resolvedKind = ResolveKind(capabilities, kind);
        ValidateRequirements(capabilities, resolvedKind);

        if (resolvedKind == StackSourceKind.Cpu)
        {
            var events = TraceAnalyzer.GetCpuSampleEvents(traceLog, fromMs, toMs);
            return CopyStackSource.Clone(new TraceEventStackSource(events));
        }

        if (resolvedKind == StackSourceKind.ActivityCpu)
        {
            var activityCpuSource = CreateCpuActivityStackSource(traceLog);
            return FilterByTime(activityCpuSource, fromMs, toMs);
        }

        bool isActivity = resolvedKind == StackSourceKind.ActivityThreadTime;
        var stackSource = new MutableTraceEventStackSource(traceLog);
        using var symbolReader = new SymbolReader(TextWriter.Null);
#pragma warning disable CS0618 // ThreadTimeStackComputer is marked experimental, not obsolete.
        var computer = new ThreadTimeStackComputer(traceLog, symbolReader)
        {
            ExcludeReadyThread = true,
            UseTasks = isActivity,
            GroupByStartStopActivity = isActivity
        };
#pragma warning restore CS0618
        computer.GenerateThreadTimeStacks(stackSource);

        return FilterByTime(stackSource, fromMs, toMs);
    }

    private static StackSource CreateCpuActivityStackSource(Etlx.TraceLog traceLog)
    {
        // Attribute actual CPU samples without introducing simulated blocked, await, or unknown time.
        var stackSource = new MutableTraceEventStackSource(traceLog);
        using var symbolReader = new SymbolReader(TextWriter.Null);
        using var eventSource = traceLog.Events.GetSource();
        var activityComputer = new ActivityComputer(eventSource, symbolReader);
        var startStopComputer = new StartStopActivityComputer(eventSource, activityComputer);
        var sample = new StackSourceSample(stackSource);
        float defaultMetric = (float)traceLog.SampleProfileInterval.TotalMilliseconds;

        void AddSample(TraceEvent traceEvent, float metric)
        {
            if (traceEvent.ProcessID == 0)
                return;

            var thread = traceEvent.Thread();
            if (thread == null)
                return;

            sample.Metric = metric;
            sample.TimeRelativeMSec = traceEvent.TimeStampRelativeMSec;
            sample.StackIndex = activityComputer.GetCallStack(
                stackSource,
                traceEvent,
                topThread => startStopComputer.GetCurrentStartStopActivityStack(
                    stackSource, thread, topThread));
            stackSource.AddSample(sample);
        }

        eventSource.Kernel.PerfInfoSample += data => AddSample(data, defaultMetric);

        var sampleProfiler = new SampleProfilerTraceEventParser(eventSource);
        sampleProfiler.ThreadSample += data =>
        {
            if (data.Type == ClrThreadSampleType.Managed)
                AddSample(data, defaultMetric);
        };

        var universalEvents = new UniversalEventsTraceEventParser(eventSource);
        universalEvents.cpu += data => AddSample(data, data.Value);

        eventSource.Process();
        stackSource.DoneAddingSamples();
        return stackSource;
    }

    private static StackSource FilterByTime(
        StackSource stackSource,
        double? fromMs,
        double? toMs)
    {
        if (!fromMs.HasValue && !toMs.HasValue)
            return stackSource;

        var filter = new FilterParams
        {
            StartTimeRelativeMSec = fromMs?.ToString(CultureInfo.CurrentCulture) ?? "",
            EndTimeRelativeMSec = toMs?.ToString(CultureInfo.CurrentCulture) ?? ""
        };
        return new FilterStackSource(filter, stackSource, ScalingPolicyKind.TimeMetric);
    }

    private static StackSourceKind ResolveKind(TraceCapabilities capabilities, StackSourceKind kind)
    {
        if (kind != StackSourceKind.Activity)
            return kind;

        return capabilities.SupportsThreadTime
            ? StackSourceKind.ActivityThreadTime
            : StackSourceKind.ActivityCpu;
    }

    private static void ValidateRequirements(TraceCapabilities capabilities, StackSourceKind kind)
    {
        if (kind == StackSourceKind.Cpu && !capabilities.SupportsCpuStacks)
        {
            throw new InvalidOperationException(
                "CPU stack analysis requires sampled-profile events. " +
                "Collect with PerfView CPU sampling or dotnet-trace's sample profiler.");
        }

        if (kind is StackSourceKind.ThreadTime or StackSourceKind.ActivityThreadTime &&
            !capabilities.SupportsThreadTime)
        {
            throw new InvalidOperationException(
                $"{GetDisplayName(kind)} analysis requires context-switch events. " +
                "Collect the trace with PerfView /ThreadTime.");
        }

        if (kind == StackSourceKind.ActivityCpu && !capabilities.SupportsCpuStacks)
        {
            throw new InvalidOperationException(
                "CPU-attributed activity analysis requires sampled-profile events. " +
                "Collect with PerfView CPU sampling or dotnet-trace's sample profiler.");
        }

        if (kind is StackSourceKind.ActivityCpu or StackSourceKind.ActivityThreadTime &&
            capabilities.StartStopEventCount == 0)
        {
            throw new InvalidOperationException(
                "Start/Stop activity analysis requires EventSource Start/Stop events with activity IDs. " +
                "Enable the relevant application providers during collection.");
        }
    }

    public static StackSourceKind ParseKind(string value) => value.ToLowerInvariant() switch
    {
        "cpu" => StackSourceKind.Cpu,
        "threadtime" => StackSourceKind.ThreadTime,
        "activity" => StackSourceKind.Activity,
        "activity-cpu" => StackSourceKind.ActivityCpu,
        "activity-threadtime" => StackSourceKind.ActivityThreadTime,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown stack source.")
    };

    public static string GetName(StackSourceKind kind) => kind switch
    {
        StackSourceKind.ThreadTime => "thread-time",
        StackSourceKind.Activity => "activity",
        StackSourceKind.ActivityCpu => "activity-cpu",
        StackSourceKind.ActivityThreadTime => "activity-thread-time",
        _ => "cpu"
    };

    public static string GetDisplayName(StackSourceKind kind) => kind switch
    {
        StackSourceKind.ThreadTime => "Thread Time",
        StackSourceKind.Activity => "Start/Stop Activity (Auto)",
        StackSourceKind.ActivityCpu => "Start/Stop Activity (CPU)",
        StackSourceKind.ActivityThreadTime => "Start/Stop Activity (Thread Time)",
        _ => "CPU"
    };
}
