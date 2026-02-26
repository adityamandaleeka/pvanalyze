using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Diagnostics.Tracing.Stacks;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Server;

public class TraceSession : IDisposable
{
    public string Id { get; }
    public string FilePath { get; }
    public Etlx.TraceLog TraceLog { get; }
    public string EtlxPath { get; }
    public string? ExtractedPath { get; }

    // Cached call tree (expensive to build)
    private CallTree? _cachedCallTree;
    private readonly object _callTreeLock = new();

    public TraceSession(string id, string filePath, Etlx.TraceLog traceLog, string etlxPath, string? extractedPath = null)
    {
        Id = id;
        FilePath = filePath;
        TraceLog = traceLog;
        EtlxPath = etlxPath;
        ExtractedPath = extractedPath;
    }

    public CallTree GetOrBuildCallTree()
    {
        if (_cachedCallTree != null) return _cachedCallTree;
        lock (_callTreeLock)
        {
            if (_cachedCallTree != null) return _cachedCallTree;

            var traceStackSource = new TraceEventStackSource(TraceLog.Events);
            var stackSource = CopyStackSource.Clone(traceStackSource);
            var callTree = new CallTree(ScalingPolicyKind.TimeMetric);
            callTree.StackSource = stackSource;
            _cachedCallTree = callTree;
            return _cachedCallTree;
        }
    }

    public void Dispose()
    {
        TraceLog.Dispose();
        try { File.Delete(EtlxPath); } catch { }
        if (ExtractedPath != null)
            try { Directory.Delete(ExtractedPath, true); } catch { }
    }
}

public class TraceSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, TraceSession> _sessions = new();

    public string OpenTrace(string filePath)
    {
        string? extractedPath = null;
        var actualPath = filePath;

        // Auto-extract .zip files (e.g. .etl.zip)
        if (filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "perfviewx_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(filePath, tempDir);
            var extracted = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase));
            if (extracted == null)
                throw new InvalidOperationException($"No .etl or .nettrace file found inside {Path.GetFileName(filePath)}");
            actualPath = extracted;
            extractedPath = tempDir;
        }

        // ETL files are Windows-only
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows)
            && actualPath.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            if (extractedPath != null)
                try { Directory.Delete(extractedPath, true); } catch { }
            throw new InvalidOperationException(
                "ETL files can only be opened on Windows. On macOS/Linux, use .nettrace files captured with 'dotnet-trace collect'.");
        }

        string etlxPath;
        if (actualPath.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase))
        {
            etlxPath = Etlx.TraceLog.CreateFromEventPipeDataFile(actualPath);
        }
        else
        {
            // ETL → ETLX conversion (Windows only)
            etlxPath = Path.ChangeExtension(actualPath, ".etlx");
            Etlx.TraceLog.CreateFromEventTraceLogFile(actualPath, etlxPath);
        }
        var traceLog = new Etlx.TraceLog(etlxPath);
        var id = Guid.NewGuid().ToString("N")[..12];
        var session = new TraceSession(id, filePath, traceLog, etlxPath, extractedPath);
        _sessions[id] = session;
        return id;
    }

    public TraceSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public void CloseSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Dispose();
        }
    }

    public IReadOnlyCollection<TraceSessionInfo> ListSessions()
    {
        return _sessions.Values.Select(s => new TraceSessionInfo(s.Id, s.FilePath)).ToList();
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }
}
