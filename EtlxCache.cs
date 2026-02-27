using Microsoft.Diagnostics.Tracing.Etlx;
using System.Diagnostics;

namespace PVAnalyze;

/// <summary>
/// Caches the .nettrace → .etlx conversion so repeated commands on the same trace
/// don't re-parse the file. The .etlx is kept alongside the .nettrace and reused
/// if it's newer than the source.
/// </summary>
public static class EtlxCache
{
    private const string CacheSuffix = ".pvanalyze.etlx";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private const int LockRetryDelayMs = 50;

    public static string GetOrCreateEtlx(string nettraceFilePath)
    {
        string etlxPath = nettraceFilePath + CacheSuffix;
        string lockPath = etlxPath + ".lock";

        if (IsFreshCache(nettraceFilePath, etlxPath))
            return etlxPath;

        using var lockStream = AcquireLock(lockPath);
        if (IsFreshCache(nettraceFilePath, etlxPath))
            return etlxPath;

        string tempPath = $"{etlxPath}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            TraceLog.CreateFromEventPipeDataFile(nettraceFilePath, tempPath);
            try
            {
                File.Move(tempPath, etlxPath, overwrite: true);
                return etlxPath;
            }
            catch (IOException publishEx)
            {
                if (!IsFreshCache(nettraceFilePath, etlxPath))
                    throw new IOException($"Failed to publish ETLX cache '{etlxPath}'.", publishEx);

                // Another writer published a fresh cache first.
                TryDeleteTemp(tempPath);
                return etlxPath;
            }
        }
        catch (Exception ex)
        {
            try
            {
                TryDeleteTemp(tempPath);
            }
            catch (Exception cleanupEx)
            {
                throw new IOException(
                    $"Failed to clean temporary ETLX cache file '{tempPath}' after conversion failure.",
                    new AggregateException(ex, cleanupEx));
            }
            throw;
        }
    }

    private static bool IsFreshCache(string nettraceFilePath, string etlxPath)
    {
        if (!File.Exists(etlxPath))
            return false;

        var nettraceTime = File.GetLastWriteTimeUtc(nettraceFilePath);
        var etlxTime = File.GetLastWriteTimeUtc(etlxPath);
        return etlxTime >= nettraceTime;
    }

    private static FileStream AcquireLock(string lockPath)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (sw.Elapsed < LockTimeout)
            {
                Thread.Sleep(LockRetryDelayMs);
            }
            catch (UnauthorizedAccessException) when (sw.Elapsed < LockTimeout)
            {
                Thread.Sleep(LockRetryDelayMs);
            }
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }
}
