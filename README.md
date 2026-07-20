# pvanalyze

A cross-platform command-line tool for analyzing .NET performance traces from
`dotnet-trace` (`.nettrace`) and PerfView (`.etl`, `.etl.zip`, and `.etlx`).

> Point your coding agent at this repo and let it work. There's intentionally no `SKILL.md` or `AGENTS.md` — `--help` and this README are enough context for today's frontier models, based on my experience.

## Overview

`pvanalyze` runs on **macOS, Linux, and Windows**. Use PerfView to collect traces
on Windows and `dotnet-trace` to collect them on macOS and Linux. Analysis with
`pvanalyze` is cross-platform. The CLI is ideal for:

- Automation and scripting
- CI/CD pipelines
- AI/LLM agent integration
- Developers on non-Windows platforms

## Installation

Run directly with `dnx` after the package is published to NuGet:

```bash
dnx pvanalyze -- info trace.nettrace
```

Build and run from source:

```bash
dotnet pack -c Release
dnx pvanalyze --source ./bin/Release --version 0.1.0 -- info trace.nettrace

# Or publish as a self-contained executable
dotnet publish -c Release -r osx-arm64 --self-contained
```

## Usage

### Collect a Trace

#### Windows: PerfView

PerfView is the recommended collector on Windows. It can capture ETW kernel and
runtime events, native stacks, context switches, and hardware counters that are
not available in a standard EventPipe trace.

Choose a capture profile based on the question being investigated. Collecting
more events has overhead, so do not use `/ThreadTime`, hardware counters, or
additional providers unless that data is needed.

| Investigation | PerfView capture | pvanalyze analysis |
|---|---|---|
| CPU hotspots | `PerfView collect trace.etl.zip` | `cpustacks --stack-source cpu` |
| Blocking and off-CPU time | `PerfView /ThreadTime collect trace.etl.zip` | `stacks --stack-source threadtime` |
| CPU used by async activities | `PerfView /Providers:*MyProvider collect trace.etl.zip` | `stacks --stack-source activity-cpu --inclusive` |
| End-to-end async activity time | `PerfView /ThreadTime /Providers:*MyProvider collect trace.etl.zip` | `stacks --stack-source activity-threadtime --inclusive` |
| Hardware counter samples | `PerfView /CpuCounters:Counter:Interval collect trace.etl.zip` | `events --type PMCSample` |
| GC, allocation, JIT, exceptions | Default PerfView CLR providers, or targeted provider keywords | `gcstats`, `alloc`, `jitstats`, `exceptions` |
| Arbitrary provider events | `/Providers:<provider-spec>` | `events` |

Use `PerfView listCpuCounters` to discover hardware counters and valid sampling
intervals for the current machine.

```powershell
# Stop collection by pressing S in the PerfView console.
PerfView /AcceptEula /NoGui collect trace.etl.zip
PerfView /AcceptEula /NoGui /ThreadTime collect threadtime.etl.zip
```

#### macOS and Linux: dotnet-trace

Use `dotnet-trace` to collect EventPipe traces:

```bash
# Install dotnet-trace (one-time)
dotnet tool install --global dotnet-trace

# Collect a trace from a running process
dotnet-trace collect --process-id <PID> --output trace.nettrace

# Or collect while running an app
dotnet-trace collect -- dotnet run
```

All analysis commands accept `.nettrace`, `.etl`, `.etl.zip`, and `.etlx`
inputs. Raw and zipped traces are converted to an ETLX cache beside the source
file; use `pvanalyze clean <trace-file>` to remove that cache.

Run `pvanalyze info <trace-file>` first. In addition to trace metadata and
processes, it reports whether the captured events support CPU stacks,
thread-time, async activities, hardware-counter inspection, GC, allocations,
exceptions, and JIT analysis. `stacks` and `calltree` reject stack sources whose
required events are absent instead of returning incomplete results.

### Analyze with pvanalyze

```bash
# Show trace information
pvanalyze info trace.nettrace
pvanalyze info trace.etl.zip

# GC statistics (summary)
pvanalyze gcstats trace.nettrace
pvanalyze gcstats trace.nettrace --format json

# GC timeline (per-GC breakdown)
pvanalyze gcstats trace.nettrace --timeline
pvanalyze gcstats trace.nettrace --longest 5   # Top 5 longest pauses

# GC with time filtering
pvanalyze gcstats trace.nettrace --from 1000 --to 2000 --timeline

# DATAS (Dynamic Adaptation) — heap count tuning decisions
pvanalyze datas trace.nettrace                    # overview + tuning timeline
pvanalyze datas trace.nettrace --changes-only     # only heap count transitions
pvanalyze datas trace.nettrace --samples           # per-GC budget/TCP/MSL samples
pvanalyze datas trace.nettrace --gen2              # gen2 backstop tuning
pvanalyze datas trace.nettrace --changes-only --format json

# JIT compilation statistics
pvanalyze jitstats trace.nettrace
pvanalyze jitstats trace.nettrace --format json

# CPU stacks analysis
pvanalyze cpustacks trace.nettrace --top 20
pvanalyze cpustacks trace.etl.zip --top 20
pvanalyze cpustacks trace.nettrace --format json

# Thread-time stacks include CPU and blocked time from PerfView context switches
pvanalyze stacks trace.etl.zip --stack-source threadtime --inclusive

# Attribute sampled CPU to EventSource Start/Stop activities
pvanalyze stacks trace.etl.zip --stack-source activity-cpu --inclusive

# Include CPU, blocked, runnable, task, and await time under each activity
pvanalyze stacks trace.etl.zip --stack-source activity-threadtime --inclusive
pvanalyze calltree trace.etl.zip --stack-source activity-threadtime --hot-path

# Export to SpeedScope for flame graph visualization
pvanalyze cpustacks trace.nettrace --format speedscope
# Then open at https://www.speedscope.app/

# List all event types in the trace
pvanalyze events trace.nettrace --list

# Filter events by type or provider
pvanalyze events trace.nettrace --type GCStart
pvanalyze events trace.nettrace --provider DotNETRuntime --limit 50

# Inspect hardware-counter samples from PerfView /CpuCounters collection
pvanalyze events trace.etl.zip --type PMCSample

# Filter by PID, TID, or payload content
pvanalyze events trace.nettrace --pid 1234
pvanalyze events trace.nettrace --payload "ConnectionReset"

# Time-filtered events
pvanalyze events trace.nettrace --from 1000 --to 2000

# Exception analysis
pvanalyze exceptions trace.nettrace
pvanalyze exceptions trace.nettrace --type NullReference

# CPU call tree analysis
pvanalyze calltree trace.nettrace --depth 5
pvanalyze calltree trace.nettrace --hot-path
pvanalyze calltree trace.nettrace --caller-callee "WriteAsJsonAsync"
pvanalyze calltree trace.nettrace --hot-path --format json
```

## Commands

### `info <trace-file>`

Display basic trace metadata:
- Duration, event count, and processes
- Available analyses inferred from captured events
- Event counts supporting each available analysis

### `gcstats <trace-file>`

Analyze garbage collection performance:
- Summary stats: total GCs, allocations, pause times
- Timeline mode (`--timeline`): per-GC breakdown
- Longest pauses (`--longest N`)
- Time filtering (`--from`, `--to`)

Options:
- `--format text|json` - Output format
- `--process <name>` - Filter by process
- `--timeline` - Show per-GC events
- `--longest <N>` - Show N longest pauses
- `--from <ms>` / `--to <ms>` - Time range filter

### `jitstats <trace-file>`

Analyze JIT compilation.

### `cpustacks|stacks <trace-file>`

Analyze CPU, thread-time, or async activity stacks:
- Top methods by exclusive or inclusive metric
- Thread-time analysis including blocked time from ETW context switches
- Start/Stop activity grouping with task and await-time attribution
- Group by module or namespace
- SpeedScope export for flame graphs

Options:
- `--format text|json|speedscope`
- `--top <N>` - Number of entries to show
- `--group-by method|module|namespace` - Aggregation level
- `--stack-source cpu|threadtime|activity-cpu|activity-threadtime|activity`
  - `cpu`: sampled on-CPU stacks
  - `threadtime`: CPU plus blocked and runnable time; requires context switches
  - `activity-cpu`: sampled CPU grouped under Start/Stop activities
  - `activity-threadtime`: full thread time grouped under Start/Stop activities
  - `activity`: automatically chooses `activity-threadtime` when context switches
    are present, otherwise `activity-cpu`
- `--inclusive` - Sort by inclusive time instead of exclusive
- `--from <ms>` / `--to <ms>` - Time range filter
- `--output <file>` - Output file

Examples:
```bash
# Top 20 methods
pvanalyze cpustacks trace.nettrace --top 20

# Group by module (assembly)
pvanalyze cpustacks trace.nettrace --group-by module --top 10

# Group by namespace, sorted by inclusive time
pvanalyze cpustacks trace.nettrace --group-by namespace --inclusive

# Analyze specific time window
pvanalyze cpustacks trace.nettrace --from 1000 --to 2000 --top 10

# Analyze blocked and on-CPU time from a PerfView /ThreadTime trace
pvanalyze stacks trace.etl.zip --stack-source threadtime --inclusive

# Attribute sampled CPU to async Start/Stop activities
pvanalyze stacks trace.etl.zip --stack-source activity-cpu --inclusive

# Attribute CPU, blocked, runnable, task, and await time to activities
pvanalyze stacks trace.etl.zip --stack-source activity-threadtime --inclusive
```

Context-switch collection is required only for off-CPU attribution:
`threadtime` and `activity-threadtime` therefore require a PerfView trace
collected with `/ThreadTime`. `activity-cpu` does not require context switches;
it needs sampled-profile events plus EventSource Start/Stop events with activity
IDs. All activity modes use TraceEvent's Start/Stop activity computer and
preserve events before a `--from` boundary so activity state is reconstructed
correctly before applying the requested time filter.

### `alloc <trace-file>`

Analyze memory allocations by type:
- Shows top allocating types with count, total bytes, and average size
- Identifies Large Object Heap (LOH) allocations
- Group by type, namespace, or module

**Note:** Requires allocation events. On Windows, collect with PerfView's CLR
providers. On macOS or Linux, use:
```bash
dotnet-trace collect --providers "Microsoft-Windows-DotNETRuntime:0x200001:5" -- dotnet run
```

Options:
- `--format text|json`
- `--top <N>` - Number of types to show
- `--group-by type|namespace|module` - Aggregation level
- `--from <ms>` / `--to <ms>` - Time range filter

### `datas <trace-file>`

Analyze DATAS (Dynamic Adaptation To Application Sizes) tuning decisions. DATAS dynamically adjusts heap count and gen0 budget on server GC. Requires .NET 9+ with `DOTNET_GCDynamicAdaptationMode=1` and GC events collected at verbose level.

**Trace collection on macOS or Linux:**
```bash
dotnet-trace collect -p <PID> --providers "Microsoft-Windows-DotNETRuntime:0x4C14FCCBD:5"
```

On Windows, pass the equivalent CLR provider and keywords to PerfView using
`/Providers`.

Options:
- `--samples` - Show per-GC samples (budget, TCP, MSL wait times)
- `--tuning` - Show heap count tuning decisions
- `--gen2` - Show gen2 full GC backstop tuning
- `--changes-only` - Only show events where heap count changed (and ±3 GC window for samples)
- `--format text|json` - Output format
- `--process <name>` - Filter by process

Examples:
```bash
# Quick overview: heap count range, changes, mean TCP
pvanalyze datas trace.nettrace

# Just the transitions — ideal for agents
pvanalyze datas trace.nettrace --changes-only

# Full detail around heap count changes
pvanalyze datas trace.nettrace --samples --changes-only

# Everything as JSON
pvanalyze datas trace.nettrace --format json
```

### `events <trace-file>`

List and filter events:
- List unique event types (`--list`)
- Filter by type, provider, PID, TID, or payload content
- Time range filtering

Options:
- `--list` - Show event type summary only
- `--type <name>` - Filter by event type
- `--provider <name>` - Filter by provider
- `--pid <id>` - Filter by process ID
- `--tid <id>` - Filter by thread ID
- `--payload <text>` - Search event payload content
- `--limit <N>` - Max events to show
- `--from <ms>` / `--to <ms>` - Time range

### `exceptions <trace-file>`

List exceptions thrown during the trace:
- Summary by exception type
- Individual exception details

Options:
- `--type <name>` - Filter by exception type
- `--from <ms>` / `--to <ms>` - Time range
- `--limit <N>` - Max exceptions to show

### `calltree <trace-file>`

Call tree analysis with hot path detection:
- Aggregated call tree with inclusive/exclusive metrics
- Hot path follows the dominant call chain
- Caller/callee view for any method (supports substring matching)

Options:
- `--depth <N>` - Max tree depth to display (default: 3)
- `--hot-path` - Follow the dominant call chain (child ≥80% of parent)
- `--caller-callee <method>` - Show callers and callees for a method
- `--format text|json` - Output format
- `--stack-source cpu|threadtime|activity-cpu|activity-threadtime|activity`
  - `activity` selects the richest supported activity mode automatically
- `--from <ms>` / `--to <ms>` - Time range filter

Examples:
```bash
# Call tree to depth 5
pvanalyze calltree trace.nettrace --depth 5

# Hot path — find where CPU time actually goes
pvanalyze calltree trace.nettrace --hot-path

# Who calls a method and what does it call?
pvanalyze calltree trace.nettrace --caller-callee "Serialize"

# JSON output for agent consumption
pvanalyze calltree trace.nettrace --hot-path --format json

# Analyze a specific time window
pvanalyze calltree trace.nettrace --hot-path --from 1000 --to 2000
```

## JSON Output for Agents

Commands that expose `--format json` produce machine-readable output:

```bash
pvanalyze gcstats trace.nettrace --format json
pvanalyze events trace.nettrace --list --format json
```

## Time Range Filtering

Most commands support `--from` and `--to` for analyzing specific time windows:

```bash
# Analyze GCs between 1-2 seconds into the trace
pvanalyze gcstats trace.nettrace --from 1000 --to 2000

# List events in a time window
pvanalyze events trace.nettrace --from 500 --to 1000 --type GC
```

## Requirements

- .NET 10.0 or later

## Related Tools

- [PerfView](https://github.com/microsoft/perfview) - Recommended trace collector on Windows
- [dotnet-trace](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace) - EventPipe trace collection on macOS and Linux
- [SpeedScope](https://www.speedscope.app/) - Interactive flame graph visualization
