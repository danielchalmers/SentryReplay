using System.IO;
using System.IO.Compression;

namespace SentryDeck.Tests;

public sealed class PackageManagerTests : IDisposable
{
    private const string ArchiveRoot = "ffmpeg-n9.0-latest-win64-gpl-shared-9.0";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"SentryDeckTests-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Nested below the scratch root so everything a test writes lands inside the directory Dispose deletes.
    private string DestinationBinPath => Path.Combine(_root, "install", "ffmpeg-bin");

    private string WriteArchive(params string[] entryNames)
    {
        var zipPath = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var entryName in entryNames)
        {
            var entry = archive.CreateEntry(entryName);

            // A name ending in "/" is a bare directory entry, which real FFmpeg archives carry.
            if (entryName.EndsWith('/'))
            {
                continue;
            }

            using var stream = entry.Open();
            stream.Write("payload"u8);
        }

        return zipPath;
    }

    [Fact]
    public void ExtractFFmpegBin_SkipsEntriesOutsideTheBinPrefix()
    {
        var zipPath = WriteArchive(
            $"{ArchiveRoot}/bin/",
            $"{ArchiveRoot}/bin/ffmpeg.exe",
            $"{ArchiveRoot}/bin/sub/avcodec.dll",
            $"{ArchiveRoot}/doc/README");

        var extracted = PackageManager.ExtractFFmpegBin(zipPath, DestinationBinPath, ArchiveRoot);

        // Only the two real binaries count.
        // The returned count is the only signal the caller logs, so a bare directory entry inflating it would make an unusable install look successful.
        extracted.ShouldBe(2);
        File.Exists(Path.Combine(DestinationBinPath, "ffmpeg.exe")).ShouldBeTrue();
        Directory.GetFiles(DestinationBinPath, "README", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public void ExtractFFmpegBin_MatchesThePrefixCaseInsensitively()
    {
        // The prefix is derived from the download URL, not from the archive, so a casing mismatch between the two would otherwise leave an empty bin folder and no FFmpeg at all.
        var zipPath = WriteArchive($"{ArchiveRoot.ToUpperInvariant()}/BIN/ffmpeg.exe");

        var extracted = PackageManager.ExtractFFmpegBin(zipPath, DestinationBinPath, ArchiveRoot);

        extracted.ShouldBe(1);
        File.Exists(Path.Combine(DestinationBinPath, "ffmpeg.exe")).ShouldBeTrue();
    }

    [Fact]
    public void ExtractFFmpegBin_RejectsEntriesThatEscapeTheDestination()
    {
        // Entry names come verbatim out of a downloaded archive and the destination sits next to the app's own binaries, so a "../" walk must be dropped rather than written.
        // Forward slashes because that is what the ZIP format mandates and what the real builds emit.
        var zipPath = WriteArchive(
            $"{ArchiveRoot}/bin/ffmpeg.exe",
            $"{ArchiveRoot}/bin/../../evil.dll");

        var extracted = PackageManager.ExtractFFmpegBin(zipPath, DestinationBinPath, ArchiveRoot);

        extracted.ShouldBe(1);
        Directory.GetFiles(_root, "evil.dll", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public void ExtractFFmpegBin_ClearsAnExistingDestination()
    {
        // A leftover DLL from an earlier FFmpeg release must not survive alongside the new ones; Flyleaf loads whatever is in this folder and a mixed set fails at load time.
        Directory.CreateDirectory(DestinationBinPath);
        var stalePath = Path.Combine(DestinationBinPath, "avcodec-60.dll");
        File.WriteAllText(stalePath, "stale");

        var zipPath = WriteArchive($"{ArchiveRoot}/bin/ffmpeg.exe");
        PackageManager.ExtractFFmpegBin(zipPath, DestinationBinPath, ArchiveRoot);

        File.Exists(stalePath).ShouldBeFalse();
        File.Exists(Path.Combine(DestinationBinPath, "ffmpeg.exe")).ShouldBeTrue();
    }
}
