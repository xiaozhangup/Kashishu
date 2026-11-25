using System.Net.WebSockets;
using System.Text.Json;

namespace Kashishu;

internal sealed class SoloistSocket : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private PendingCommand? _pending;
    private int _stateRequested;

    private sealed record PendingCommand(string Name, TaskCompletionSource<string?> Completion);

    public async Task ConnectAsync(Uri endpoint, CancellationToken token)
    {
        _socket.Options.Proxy = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await _socket.ConnectAsync(endpoint, timeout.Token);
    }

    public async Task ReceiveAsync(Action<JsonElement> onEvent, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var part = await _socket.ReceiveAsync(buffer.AsMemory(), token);
                if (part.MessageType == WebSocketMessageType.Close)
                    break;
                if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 1024 * 1024)
                    throw new InvalidOperationException("Soloist 返回了无效的 WebSocket 消息");
                message.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage)
                    continue;

                using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                var root = json.RootElement;
                var pending = Volatile.Read(ref _pending);
                switch (SoloistService.String(root, "type"))
                {
                    case "command_result":
                        if (pending?.Name == SoloistService.String(root, "command"))
                            pending?.Completion.TrySetResult(null);
                        break;
                    case "error":
                        pending?.Completion.TrySetResult("Soloist 拒绝了播放操作");
                        break;
                    case "playback_state":
                        onEvent(root);
                        if (pending?.Name == "get_state")
                            pending.Completion.TrySetResult(null);
                        break;
                    default:
                        onEvent(root);
                        break;
                }
                message.SetLength(0);
            }
        }
        finally
        {
            // Prevent a command racing with disconnect from waiting for an acknowledgment.
            _socket.Abort();
            Volatile.Read(ref _pending)?.Completion.TrySetResult("Soloist 连接已断开");
        }
    }

    public void RequestState(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _stateRequested, 1) == 0)
            _ = RequestAsync();

        async Task RequestAsync()
        {
            try { await SendAsync("get_state", null, null, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { Volatile.Write(ref _stateRequested, 0); }
        }
    }

    public async Task<string?> SendAsync(
        string command, string? field, object? argument, CancellationToken token)
    {
        await _commands.WaitAsync(token);
        try
        {
            if (_socket.State != WebSocketState.Open)
                return "Soloist 连接已断开";
            var message = new Dictionary<string, object?> { ["type"] = "command", ["command"] = command };
            if (field is not null)
                message.Add(field, argument);
            var pending = new PendingCommand(command, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _pending, pending);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message).AsMemory(),
                    WebSocketMessageType.Text, true, timeout.Token);
                return await pending.Completion.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // A late result has no request ID. Reconnect before sending another command.
                _socket.Abort();
                token.ThrowIfCancellationRequested();
                return "Soloist 没有及时确认操作，请重试";
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
            {
                return "Soloist 连接已断开";
            }
        }
        finally
        {
            Volatile.Write(ref _pending, null);
            _commands.Release();
        }
    }

    public void Dispose() => _socket.Dispose();
}
