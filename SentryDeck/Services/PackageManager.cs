using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Locates and installs the FFmpeg binaries required by Flyleaf.
/// </summary>
public static class PackageManager
{
    private const string FFmpegReleaseBranch = "9.0";
    private static readonly string FFmpegBinFolderName = $"ffmpeg-{FFmpegReleaseBranch}-bin";

    private static string FFmpegInstallRoot => AppContext.BaseDirectory;

    private static async Task<long> DownloadFile(string url, string savePath)
    {
        // This download is the hard first-run gate (no clips play without FFmpeg), so it must not fail just because the connection is slow: the default 100s HttpClient.Timeout caps the WHOLE transfer, and the default ResponseContentRead buffers the entire archive in memory before a byte hits disk.
        // Stream to the file instead, with a generous overall ceiling that only a dead transfer should ever hit.
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30),
        };

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var fileStream = File.Create(savePath);
        await response.Content.CopyToAsync(fileStream);

        return fileStream.Length;
    }

    internal static int ExtractFFmpegBin(string zipFilePath, string destinationBinPath, string archiveRoot)
    {
        if (Directory.Exists(destinationBinPath))
        {
            Directory.Delete(destinationBinPath, true);
        }

        Directory.CreateDirectory(destinationBinPath);
        var extractedFileCount = 0;

        var binPrefix = $"{archiveRoot}/bin/";
        using var archive = ZipFile.OpenRead(zipFilePath);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(binPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = entry.FullName[binPrefix.Length..];
            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var outputPath = Path.Combine(destinationBinPath, relativePath);

            // Entry names come straight out of a downloaded archive, and the destination sits next to the app's own binaries, so a name that walks up with ".." would overwrite them.
            // Checked before the directory is created so the escaping path is never materialized.
            var fullOutputPath = Path.GetFullPath(outputPath);
            if (!fullOutputPath.StartsWith(Path.GetFullPath(destinationBinPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Skipping archive entry that escapes the destination. Entry={Entry}", entry.FullName);
                continue;
            }

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            entry.ExtractToFile(outputPath, true);
            extractedFileCount++;
        }

        return extractedFileCount;
    }

    /// <summary>
    /// Downloads the supported shared FFmpeg build and extracts its bin folder.
    /// </summary>
    public static async Task DownloadAndExtractFFmpeg()
    {
        var destinationBinPath = Path.Combine(FFmpegInstallRoot, FFmpegBinFolderName);
        var url = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n{FFmpegReleaseBranch}-latest-win64-gpl-shared-{FFmpegReleaseBranch}.zip",
            Architecture.Arm64 => $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n{FFmpegReleaseBranch}-latest-winarm64-gpl-shared-{FFmpegReleaseBranch}.zip",
            _ => throw new NotSupportedException($"FFmpeg download is not supported for {RuntimeInformation.ProcessArchitecture}."),
        };
        var tempPath = Path.GetTempFileName();
        var archiveRoot = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Log.Information(
                "Downloading FFmpeg. Url={Url}; TempPath={TempPath}; Architecture={Architecture}",
                url,
                tempPath,
                RuntimeInformation.ProcessArchitecture);
            var bytesDownloaded = await DownloadFile(url, tempPath);
            Log.Information(
                "Downloaded FFmpeg archive. TempPath={TempPath}; Bytes={Bytes}; ElapsedMs={ElapsedMs}",
                tempPath,
                bytesDownloaded,
                stopwatch.ElapsedMilliseconds);

            Log.Information(
                "Extracting FFmpeg binaries. TempPath={TempPath}; Destination={Destination}; ArchiveRoot={ArchiveRoot}",
                tempPath,
                destinationBinPath,
                archiveRoot);
            var extractedFileCount = ExtractFFmpegBin(tempPath, destinationBinPath, archiveRoot);

            Log.Information(
                "FFmpeg binaries are ready. Destination={Destination}; ExtractedFileCount={ExtractedFileCount}; ElapsedMs={ElapsedMs}",
                destinationBinPath,
                extractedFileCount,
                stopwatch.ElapsedMilliseconds);

            RemoveSupersededFFmpegDirectories(FFmpegInstallRoot, FFmpegBinFolderName);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Deletes FFmpeg folders left behind by earlier release branches.
    /// Every branch installs into a folder of its own next to the app, so an upgrade otherwise leaves the previous build (about 200 MB unpacked) sitting there forever with nothing that will ever load it again.
    /// Only runs after a successful install, and a folder that refuses to delete is logged rather than failing the install: the new binaries are already in place and usable.
    /// </summary>
    internal static int RemoveSupersededFFmpegDirectories(string installRoot, string currentBinFolderName)
    {
        var removedCount = 0;

        // Materialized before the first delete: enumerating a directory while removing entries from it is not something to rely on.
        foreach (var directory in Directory.GetDirectories(installRoot, "ffmpeg-*-bin"))
        {
            if (string.Equals(Path.GetFileName(directory), currentBinFolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removedCount++;
                Log.Information("Removed superseded FFmpeg binaries. Directory={Directory}", directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Failed to remove superseded FFmpeg binaries. Directory={Directory}", directory);
            }
        }

        return removedCount;
    }

    /// <summary>
    /// Returns the installed FFmpeg bin directory, or null when it is missing.
    /// </summary>
    public static string FindFFmpegDirectory()
    {
        var binPath = Path.Combine(FFmpegInstallRoot, FFmpegBinFolderName);

        if (File.Exists(Path.Combine(binPath, "ffmpeg.exe")))
        {
            return binPath;
        }

        return null;
    }
}
