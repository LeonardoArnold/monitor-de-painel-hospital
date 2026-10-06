using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GHospPanelWatchdog.Edge;

internal sealed class CdpException(string message) : Exception(message);

/// <summary>
/// Cliente mínimo do Chrome DevTools Protocol sobre um único WebSocket local (127.0.0.1),
/// em modo "flatten" (sessões de página multiplexadas na conexão do navegador).
/// IMPORTANTE: payloads nunca são registrados em log (podem conter credenciais).
/// </summary>
internal sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private Task? _receiveLoop;

    /// <summary>Eventos CDP: (método, sessionId, params).</summary>
    public event Action<string, string?, JsonNode?>? EventReceived;

    public bool IsOpen => _ws.State == WebSocketState.Open && !_cts.IsCancellationRequested;

    public static async Task<CdpClient> ConnectAsync(int port, string browserPath, TimeSpan timeout)
    {
        var c = new CdpClient();
        c._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(timeout);
        await c._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{browserPath}"), cts.Token).ConfigureAwait(false);
        c._receiveLoop = Task.Run(c.ReceiveLoopAsync);
        return c;
    }

    public async Task<JsonNode?> SendAsync(string method, JsonObject? parameters = null, string? sessionId = null, TimeSpan? timeout = null)
    {
        if (!IsOpen) throw new CdpException("Conexão CDP fechada");
        int id = Interlocked.Increment(ref _nextId);
        var msg = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters != null) msg["params"] = parameters;
        if (sessionId != null) msg["sessionId"] = sessionId;

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false); }
            finally { _sendLock.Release(); }

            var t = timeout ?? TimeSpan.FromSeconds(10);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(t, _cts.Token)).ConfigureAwait(false);
            if (done != tcs.Task) throw new TimeoutException($"CDP {method}: sem resposta em {t.TotalSeconds:0}s");
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                    if (ms.Length > 32 * 1024 * 1024) throw new CdpException("Mensagem CDP grande demais");
                } while (!r.EndOfMessage);

                JsonNode? node;
                try { node = JsonNode.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length)); }
                catch (JsonException) { continue; }
                if (node is not JsonObject obj) continue;

                if (obj.TryGetPropertyValue("id", out var idNode) && idNode != null)
                {
                    if (_pending.TryRemove(idNode.GetValue<int>(), out var tcs))
                    {
                        if (obj.TryGetPropertyValue("error", out var err) && err != null)
                            tcs.TrySetException(new CdpException(err["message"]?.GetValue<string>() ?? "erro CDP"));
                        else
                            tcs.TrySetResult(obj["result"]);
                    }
                }
                else if (obj["method"]?.GetValue<string>() is { } method)
                {
                    try { EventReceived?.Invoke(method, obj["sessionId"]?.GetValue<string>(), obj["params"]); }
                    catch { }
                }
            }
        }
        catch
        {
            // Conexão caiu (Edge fechado/travado). Quem usa detecta por IsOpen/timeout.
        }
        finally
        {
            _cts.Cancel();
            foreach (var kv in _pending) kv.Value.TrySetException(new CdpException("Conexão CDP encerrada"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var t = new CancellationTokenSource(1000);
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, t.Token).ConfigureAwait(false);
            }
        }
        catch { }
        _ws.Dispose();
        if (_receiveLoop != null) { try { await _receiveLoop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
