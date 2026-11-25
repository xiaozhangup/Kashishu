using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Kashishu;

internal static class SoloistExecutable
{
    private const UnixFileMode Execute =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly HttpClient Http = new();
    private static string FileName => OperatingSystem.IsWindows() ? "soloist.exe" : "soloist";

    private static string ManagedPath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(root, "kashishu", "soloist", FileName);
        }
    }

    public static IEnumerable<string> FindInstalled(string previousPath)
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return pathEntries.Select(directory => Path.Combine(directory, FileName))
            .Prepend(previousPath)
            .Append(ManagedPath)
            .Where(IsExecutable)
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private static bool IsExecutable(string path) =>
        File.Exists(path) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & Execute) != 0);

    public static async Task<string> DownloadAsync(CancellationToken token)
    {
        var archive = GetArchive();
        var url = new Uri("https://soloist-builds.spotifycdn.com/" + archive);
        var destination = ManagedPath;
        var directory = Path.GetDirectoryName(destination)!;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, PrivateDirectory);

        var temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var download = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using (var executable = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                if (archive.EndsWith(".zip", StringComparison.Ordinal))
                    await CopyZipExecutableAsync(download, executable, timeout.Token);
                else
                    await CopyTarExecutableAsync(download, executable, timeout.Token);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, PrivateDirectory);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Soloist 下载超时，请在设置中重新连接");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException("Soloist 下载失败，请检查网络后重试", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException("Soloist 官方安装包无法读取，请在设置中重新连接", exception);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task CopyTarExecutableAsync(Stream download, Stream executable, CancellationToken token)
    {
        await using var gzip = new GZipStream(download, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gzip, leaveOpen: true);
        while (await tar.GetNextEntryAsync(cancellationToken: token) is { } entry)
        {
            if (Path.GetFileName(entry.Name) != FileName || entry.Length == 0
                || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.DataStream is not { } data)
                continue;
            await data.CopyToAsync(executable, token);
            return;
        }
        throw new InvalidDataException("Missing Soloist executable");
    }

    private static async Task CopyZipExecutableAsync(Stream download, Stream executable, CancellationToken token)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"kashishu-soloist-{Guid.NewGuid():N}.zip");
        await using var file = new FileStream(scratch, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await download.CopyToAsync(file, token);
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.Entries.FirstOrDefault(item => item.Name == FileName && item.Length > 0)
            ?? throw new InvalidDataException("Missing Soloist executable");
        await using var data = entry.Open();
        await data.CopyToAsync(executable, token);
    }

    private static string GetArchive()
    {
        var architecture = RuntimeInformation.OSArchitecture;
        return (OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(), architecture) switch
        {
            (true, _, _, Architecture.X64) => "soloist_release_x86_64.tar.gz",
            (true, _, _, Architecture.Arm64) => "soloist_release_arm64.tar.gz",
            (true, _, _, Architecture.Arm) => "soloist_release_arm32.tar.gz",
            (_, true, _, Architecture.Arm64) => "soloist_release_osx_arm64.tar.gz",
            (_, _, true, Architecture.X64) => "soloist_release_windows_x86_64.zip",
            _ => throw new InvalidOperationException("Soloist 官网未提供此系统架构的构建")
        };
    }
}
