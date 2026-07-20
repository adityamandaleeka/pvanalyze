using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Parsers.Universal.Events;
using Microsoft.Diagnostics.Tracing.Session;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze;

public sealed record TraceCapabilities(
    long CpuSampleCount,
    long ContextSwitchCount,
    long StartStopEventCount,
    long HardwareCounterSampleCount,
    long GcEventCount,
    long AllocationEventCount,
    long ExceptionEventCount,
    long JitEventCount)
{
    public bool SupportsCpuStacks => CpuSampleCount > 0;
    public bool SupportsThreadTime => ContextSwitchCount > 0;
    public bool SupportsCpuActivityStacks => SupportsCpuStacks && StartStopEventCount > 0;
    public bool SupportsThreadTimeActivityStacks => SupportsThreadTime && StartStopEventCount > 0;
    public bool SupportsActivityStacks => SupportsCpuActivityStacks || SupportsThreadTimeActivityStacks;
}

public static class TraceCapabilityDetector
{
    public static TraceCapabilities Detect(Etlx.TraceLog traceLog)
    {
        long cpuSamples = 0;
        long contextSwitches = 0;
        long startStopEvents = 0;
        long hardwareCounterSamples = 0;
        long gcEvents = 0;
        long allocationEvents = 0;
        long exceptionEvents = 0;
        long jitEvents = 0;

        foreach (var traceEvent in traceLog.Events)
        {
            if (IsCpuSample(traceEvent))
                cpuSamples++;

            if (IsContextSwitch(traceEvent))
            {
                contextSwitches++;
            }

            if ((traceEvent.Opcode == TraceEventOpcode.Start ||
                 traceEvent.Opcode == TraceEventOpcode.Stop) &&
                TraceEventProviders.MaybeAnEventSource(traceEvent.ProviderGuid) &&
                (traceEvent.ActivityID != Guid.Empty ||
                 traceEvent.RelatedActivityID != Guid.Empty))
            {
                startStopEvents++;
            }

            if (traceEvent is PMCCounterProfTraceData)
                hardwareCounterSamples++;

            string eventName = traceEvent.EventName;
            if (eventName.Contains("AllocationTick", StringComparison.Ordinal) ||
                eventName.Contains("SampledObjectAllocation", StringComparison.Ordinal))
            {
                allocationEvents++;
            }

            if (eventName.Contains("Exception/Start", StringComparison.Ordinal) ||
                eventName.Contains("ExceptionThrown", StringComparison.Ordinal) ||
                eventName.Contains("FirstChanceException", StringComparison.Ordinal))
            {
                exceptionEvents++;
            }

            if (eventName.Contains("Method/JittingStarted", StringComparison.Ordinal) ||
                eventName.Contains("MethodJittingStarted", StringComparison.Ordinal))
            {
                jitEvents++;
            }

            if (eventName.Equals("GC/Start", StringComparison.Ordinal) ||
                eventName.StartsWith("GCStart", StringComparison.Ordinal) ||
                eventName.Equals("GC/Stop", StringComparison.Ordinal) ||
                eventName.StartsWith("GCEnd", StringComparison.Ordinal))
            {
                gcEvents++;
            }
        }

        return new TraceCapabilities(
            cpuSamples,
            contextSwitches,
            startStopEvents,
            hardwareCounterSamples,
            gcEvents,
            allocationEvents,
            exceptionEvents,
            jitEvents);
    }

    internal static bool IsCpuSample(TraceEvent traceEvent) =>
        traceEvent.ProcessID != 0 &&
        (traceEvent is SampledProfileTraceData ||
         traceEvent is SampleTraceData { EventName: "cpu" } ||
         traceEvent is ClrThreadSampleTraceData { Type: ClrThreadSampleType.Managed });

    internal static bool IsContextSwitch(TraceEvent traceEvent) =>
        traceEvent is CSwitchTraceData ||
        traceEvent is SampleTraceData { EventName: "cswitch" };
}
