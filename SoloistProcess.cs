using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace Kashishu;

internal sealed class SoloistProcess : IAsyncDisposable
{
    internal const string DeviceName = "Kashishu Soloist";
    private const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Process _child;
    private readonly FileStream _directoryLock;
    private readonly string _dataDirectory;

    private SoloistProcess(Process child, FileStream directoryLock, string dataDirectory)
    {
        _child = child;
        _directoryLock = directoryLock;
        _dataDirectory = dataDirectory;
    }

    public bool HasExited => _child.HasExited;

    public static SoloistProcess Start(
        string executable,
        string apiKey,
        SoloistAudioQuality quality = SoloistAudioQuality.Automatic)
    {
        var data = UserDirectory("XDG_STATE_HOME", ".local/state");
        var cache = UserDirectory("XDG_CACHE_HOME", ".cache");
        var directoryLock = new FileStream(Path.Combine(data, "kashishu.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            ApplyAudioQualityPrefs(data, quality);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "--device-name", DeviceName, "--api-key", apiKey,
                "--data-dir", data, "--cache-dir", cache, "--ws", "127.0.0.1:0"
            })
                start.ArgumentList.Add(argument);

            File.Delete(Path.Combine(data, "ws.addr"));
            File.Delete(Path.Combine(data, "ws.port"));
            var child = Process.Start(start) ?? throw new InvalidOperationException("Soloist 无法启动");
            var process = new SoloistProcess(child, directoryLock, data);
            // The command line includes a secret. Drain daemon output without forwarding it.
            _ = child.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
            return process;
        }
        catch (Win32Exception exception)
        {
            directoryLock.Dispose();
            throw new InvalidOperationException("Soloist 无法启动，请检查文件权限和系统架构", exception);
        }
        catch
        {
            directoryLock.Dispose();
            throw;
        }
    }

    public async Task<Uri> ReadEndpointAsync(CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(12));
        var addressPath = Path.Combine(_dataDirectory, "ws.addr");
        var portPath = Path.Combine(_dataDirectory, "ws.port");
        try
        {
            while (!HasExited)
            {
                if (File.Exists(addressPath) && File.Exists(portPath))
                {
                    var address = (await File.ReadAllTextAsync(addressPath, startup.Token)).Trim();
                    var port = (await File.ReadAllTextAsync(portPath, startup.Token)).Trim();
                    if (address == IPAddress.Loopback.ToString()
                        && ushort.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                        && value > 0)
                        return new UriBuilder("ws", address, value, "/").Uri;
                }
                await Task.Delay(100, startup.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Soloist 启动超时，请在设置中重新连接");
        }
        throw ExitError();
    }

    public InvalidOperationException ExitError() => _child.ExitCode switch
    {
        10 => new SoloistBuildExpiredException(),
        0 => new InvalidOperationException("Soloist 已退出，请在设置中重新连接"),
        var code => new InvalidOperationException($"Soloist 启动或运行失败（退出码 {code}），请检查 API Key 和音频服务")
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_child.HasExited)
                return;

            if (OperatingSystem.IsLinux())
            {
                SendSignal(_child.Id, 15); // SIGTERM lets Soloist persist state and remove its endpoint files.
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                try
                {
                    await _child.WaitForExitAsync(grace.Token);
                    return;
                }
                catch (OperationCanceledException) { }
            }

            _child.Kill(entireProcessTree: true);
            await _child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (InvalidOperationException) when (_child.HasExited) { }
        finally
        {
            _child.Dispose();
            _directoryLock.Dispose();
        }
    }

    private static string UserDirectory(string variable, string defaultPath)
    {
        var root = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), defaultPath);
        var directory = Path.Combine(root, "kashishu", "soloist");
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory, PrivateDirectory);
            File.SetUnixFileMode(directory, PrivateDirectory);
        }
        return directory;
    }

    internal static void ApplyAudioQualityPrefs(string dataDirectory, SoloistAudioQuality quality)
    {
        var settingsDir = Path.Combine(dataDirectory, "settings");
        if (!Directory.Exists(settingsDir))
            return;

        var targets = new List<string>();
        var rootPrefs = Path.Combine(settingsDir, "prefs");
        if (File.Exists(rootPrefs))
            targets.Add(rootPrefs);

        var usersDir = Path.Combine(settingsDir, "Users");
        if (Directory.Exists(usersDir))
        {
            foreach (var userDir in Directory.EnumerateDirectories(usersDir))
            {
                var userPrefs = Path.Combine(userDir, "prefs");
                if (File.Exists(userPrefs) || quality != SoloistAudioQuality.Automatic)
                    targets.Add(userPrefs);
            }
        }

        foreach (var path in targets)
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var updated = UpdatePrefsContent(existing, quality);
            if (File.Exists(path) && string.Equals(existing, updated, StringComparison.Ordinal))
                continue;

            var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".prefs.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, updated);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

    internal static string UpdatePrefsContent(string content, SoloistAudioQuality quality)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("audio.play_bitrate_enumeration=", StringComparison.Ordinal)
                && !line.StartsWith("audio.play_bitrate_non_metered_enumeration=", StringComparison.Ordinal)
                && !line.StartsWith("audio.sync_bitrate_enumeration=", StringComparison.Ordinal)
                && !line.StartsWith("audio.play_bitrate_non_metered_migrated=", StringComparison.Ordinal))
            .ToList();

        if (quality != SoloistAudioQuality.Automatic)
        {
            var value = (int)quality;
            lines.Add($"audio.play_bitrate_enumeration={value}");
            lines.Add($"audio.play_bitrate_non_metered_enumeration={value}");
            lines.Add("audio.play_bitrate_non_metered_migrated=true");
        }

        return lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";
    }

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int SendSignal(int pid, int signal);
}

internal sealed class SoloistBuildExpiredException() : InvalidOperationException("Soloist 官方构建已过期");
