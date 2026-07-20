using Microsoft.Diagnostics.Tracing.Etlx;
using System.Diagnostics;
using System.IO.Compression;

namespace PVAnalyze;

/// <summary>
/// Caches trace-to-ETLX conversion so repeated commands on the same trace don't
/// re-parse the file. Existing ETLX files are opened directly.
/// </summary>
public static class EtlxCache
{
    private const string CacheSuffix = ".pvanalyze.etlx";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private const int LockRetryDelayMs = 50;

    public static async Task<string> GetOrCreateEtlxAsync(string traceFilePath, CancellationToken cancellationToken = default)
    {
        if (Path.GetExtension(traceFilePath).Equals(".etlx", StringComparison.OrdinalIgnoreCase))
            return traceFilePath;

        string etlxPath = traceFilePath + CacheSuffix;
        string lockPath = etlxPath + ".lock";

        if (IsFreshCache(traceFilePath, etlxPath))
            return etlxPath;

        await using var lockStream = await AcquireLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
        if (IsFreshCache(traceFilePath, etlxPath))
            return etlxPath;

        string tempPath = $"{etlxPath}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        string? extractedEtlPath = null;
        try
        {
            string conversionInputPath = traceFilePath;
            if (IsPerfViewArchive(traceFilePath))
            {
                extractedEtlPath = tempPath + ".etl";
                await ExtractEtlAsync(traceFilePath, extractedEtlPath, cancellationToken).ConfigureAwait(false);
                conversionInputPath = extractedEtlPath;
            }

            // TraceEvent conversion APIs don't accept cancellation tokens. The Task.Run token
            // therefore only covers the queued-but-not-started window.
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(
                () => ConvertToEtlx(conversionInputPath, traceFilePath, tempPath),
                cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(tempPath, etlxPath, overwrite: true);
                return etlxPath;
            }
            catch (IOException publishEx)
            {
                if (!IsFreshCache(traceFilePath, etlxPath))
                    throw new IOException($"Failed to publish ETLX cache '{etlxPath}'.", publishEx);

                // Another writer published a fresh cache first. We've already succeeded, so clean
                // up our temp file and use the published cache rather than leaking it on cancel.
                TryDeleteTemp(tempPath);
                return etlxPath;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
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
        finally
        {
            if (extractedEtlPath != null)
                TryDeleteTemp(extractedEtlPath);
        }
    }

    private static void ConvertToEtlx(string conversionInputPath, string originalTracePath, string etlxPath)
    {
        if (IsEtl(conversionInputPath) || IsPerfViewArchive(originalTracePath))
            TraceLog.CreateFromEventTraceLogFile(conversionInputPath, etlxPath);
        else
            TraceLog.CreateFromEventPipeDataFile(conversionInputPath, etlxPath);
    }

    private static bool IsEtl(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".etl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".btl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPerfViewArchive(string path) =>
        path.EndsWith(".etl.zip", StringComparison.OrdinalIgnoreCase);

    private static async Task ExtractEtlAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var etlEntries = archive.Entries
            .Where(entry => entry.Length > 0 && IsEtl(entry.FullName))
            .ToList();

        if (etlEntries.Count != 1)
        {
            throw new InvalidDataException(
                $"PerfView archive '{archivePath}' must contain exactly one ETL file; found {etlEntries.Count}.");
        }

        await using var source = etlEntries[0].Open();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsFreshCache(string traceFilePath, string etlxPath)
    {
        if (!File.Exists(etlxPath))
            return false;

        var sourceTime = File.GetLastWriteTimeUtc(traceFilePath);
        var etlxTime = File.GetLastWriteTimeUtc(etlxPath);
        return etlxTime >= sourceTime;
    }

    private static async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
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
                await Task.Delay(LockRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (sw.Elapsed < LockTimeout)
            {
                await Task.Delay(LockRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }
}
