# pvanalyze

A cross-platform command-line tool for analyzing .NET performance traces (`.nettrace` files).

## Overview

`pvanalyze` is a companion tool to PerfView that runs on **Mac, Linux, and Windows**. It provides command-line access to trace analysis capabilities, making it ideal for:

- Automation and scripting
- CI/CD pipelines
- AI/LLM agent integration
- Developers on non-Windows platforms

## Installation

```bash
# Build from source
cd src/pvanalyze
dotnet build -c Release

# Or publish as a self-contained executable
dotnet publish -c Release -r osx-arm64 --self-contained
```

## Usage

### Collect a Trace

Use `dotnet-trace` to collect traces on any platform:

```bash
# Install dotnet-trace (one-time)
dotnet tool install --global dotnet-trace

# Collect a trace from a running process
dotnet-trace collect --process-id <PID> --output trace.nettrace

# Or collect while running an app
dotnet-trace collect -- dotnet run
```

### Analyze with pvanalyze

```bash
# Show trace information
pvanalyze info trace.nettrace

# GC statistics (summary)
pvanalyze gcstats trace.nettrace
pvanalyze gcstats trace.nettrace --format json

# GC timeline (per-GC breakdown)
pvanalyze gcstats trace.nettrace --timeline
pvanalyze gcstats trace.nettrace --longest 5   # Top 5 longest pauses

# GC with time filtering
pvanalyze gcstats trace.nettrace --from 1000 --to 2000 --timeline

# JIT compilation statistics
pvanalyze jitstats trace.nettrace
pvanalyze jitstats trace.nettrace --format json

# CPU stacks analysis
pvanalyze cpustacks trace.nettrace --top 20
pvanalyze cpustacks trace.nettrace --format json

# Export to SpeedScope for flame graph visualization
pvanalyze cpustacks trace.nettrace --format speedscope
# Then open at https://www.speedscope.app/

# List all event types in the trace
pvanalyze events trace.nettrace --list

# Filter events by type or provider
pvanalyze events trace.nettrace --type GCStart
pvanalyze events trace.nettrace --provider DotNETRuntime --limit 50

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
- Duration, event count, processes

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

### `cpustacks <trace-file>`

Analyze CPU profiling stacks:
- Top methods by exclusive CPU time
- Group by module or namespace
- SpeedScope export for flame graphs

Options:
- `--format text|json|speedscope`
- `--top <N>` - Number of entries to show
- `--group-by method|module|namespace` - Aggregation level
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
```

### `alloc <trace-file>`

Analyze memory allocations by type:
- Shows top allocating types with count, total bytes, and average size
- Identifies Large Object Heap (LOH) allocations
- Group by type, namespace, or module

**Note:** Requires trace collected with allocation events:
```bash
dotnet-trace collect --providers "Microsoft-Windows-DotNETRuntime:0x200001:5" -- dotnet run
```

Options:
- `--format text|json`
- `--top <N>` - Number of types to show
- `--group-by type|namespace|module` - Aggregation level
- `--from <ms>` / `--to <ms>` - Time range filter

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

CPU call tree analysis with hot path detection:
- Aggregated call tree with inclusive/exclusive metrics
- Hot path follows the dominant call chain
- Caller/callee view for any method (supports substring matching)

Options:
- `--depth <N>` - Max tree depth to display (default: 3)
- `--hot-path` - Follow the dominant call chain (child ≥80% of parent)
- `--caller-callee <method>` - Show callers and callees for a method
- `--format text|json` - Output format
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

All commands support `--format json` for machine-readable output:

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

- .NET 8.0 or later

## Related Tools

- [dotnet-trace](https://docs.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) - Cross-platform trace collection
- [PerfView](https://github.com/microsoft/perfview) - Full-featured Windows GUI for trace analysis
- [SpeedScope](https://www.speedscope.app/) - Interactive flame graph visualization
