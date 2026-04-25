using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenclawChat.Models;

namespace OpenclawChat.Services;

public sealed class OpenclawWsClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChallengeWaitTimeout = TimeSpan.FromMilliseconds(750);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly object _stateLock = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;
    private TaskCompletionSource<string>? _challengeTcs;
    private long? _lastEventSeq;
    private long _nextRequestId;
    private bool _connected;
    private string _connectionMessage = "已断开";
    private bool _disposed;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<EventGapDetectedEventArgs>? EventGapDetected;
    public event EventHandler<ChatEventReceivedEventArgs>? ChatEventReceived;
    public event EventHandler<AgentEventReceivedEventArgs>? AgentEventReceived;

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _connected;
            }
        }
    }

    public async Task ConnectAsync(OpenclawConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new ArgumentException("必须填写网关地址。", nameof(options));
        }

        await DisconnectAsync();

        SetConnectionState(false, "连接中...");

        var socket = new ClientWebSocket();
        var originHeader = ResolveOriginHeader(options);
        if (!string.IsNullOrWhiteSpace(originHeader))
        {
            socket.Options.SetRequestHeader("Origin", originHeader);
        }

        _socket = socket;
        _receiveLoopCts = new CancellationTokenSource();
        _challengeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastEventSeq = null;

        try
        {
            await socket.ConnectAsync(new Uri(options.Endpoint), cancellationToken);
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(socket, _receiveLoopCts.Token));

            _ = await TryWaitForChallengeAsync(cancellationToken);

            var auth = BuildAuth(options);
            var hello = await InvokeMethodAsync<GatewayHelloOk>(
                "connect",
                new GatewayConnectParams
                {
                    Auth = auth,
                    Client = new GatewayConnectClient
                    {
                        InstanceId = Guid.NewGuid().ToString("N"),
                    },
                },
                cancellationToken);

            if (hello is null || !string.Equals(hello.Type, "hello-ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("网关返回了无效的连接响应。");
            }

            var version = hello.Server?.Version?.Trim();
            var versionSuffix = string.IsNullOrWhiteSpace(version) ? string.Empty : $"（服务器 {version}）";
            SetConnectionState(true, $"已连接到网关{versionSuffix}");
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var loopCts = Interlocked.Exchange(ref _receiveLoopCts, null);
        if (loopCts is not null)
        {
            loopCts.Cancel();
            loopCts.Dispose();
        }

        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
                }
            }
            catch
            {
                // Best effort close.
            }
            finally
            {
                socket.Dispose();
            }
        }

        var loopTask = Interlocked.Exchange(ref _receiveLoopTask, null);
        if (loopTask is not null)
        {
            try
            {
                await loopTask;
            }
            catch
            {
                // Ignore receive-loop termination errors during disconnect.
            }
        }

        Interlocked.Exchange(ref _challengeTcs, null)?.TrySetCanceled();
        FailPending(new OperationCanceledException("已断开连接。"));
        _lastEventSeq = null;
        SetConnectionState(false, "已断开");
    }

    public Task<ChatHistoryResponse?> GetHistoryAsync(
        string sessionKey,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new ArgumentException("必须填写 sessionKey。", nameof(sessionKey));
        }

        return InvokeMethodAsync<ChatHistoryResponse>(
            "chat.history",
            new
            {
                sessionKey,
                limit,
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListSessionKeysAsync(CancellationToken cancellationToken = default)
    {
        var payload = await InvokeMethodRawAsync("sessions.list", new { }, cancellationToken);
        return ExtractSessionKeys(payload);
    }

    public Task<ChatSendAck?> SendChatAsync(
        string sessionKey,
        string message,
        string idempotencyKey,
        IReadOnlyList<ChatAttachmentPayload>? attachments,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new ArgumentException("必须填写 sessionKey。", nameof(sessionKey));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("必须填写 idempotencyKey。", nameof(idempotencyKey));
        }

        return InvokeMethodAsync<ChatSendAck>(
            "chat.send",
            new
            {
                sessionKey,
                message,
                idempotencyKey,
                attachments = attachments?.Count > 0 ? attachments : null,
            },
            cancellationToken);
    }

    public Task<ChatAbortAck?> AbortChatAsync(
        string sessionKey,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new ArgumentException("必须填写 sessionKey。", nameof(sessionKey));
        }

        object parameters = string.IsNullOrWhiteSpace(runId)
            ? new { sessionKey }
            : new { sessionKey, runId };

        return InvokeMethodAsync<ChatAbortAck>("chat.abort", parameters, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync();
        _sendLock.Dispose();
    }

    private static string? ResolveOriginHeader(OpenclawConnectionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Origin))
        {
            return options.Origin.Trim();
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "wss" => "https",
            "ws" => "http",
            _ => null,
        };

        if (scheme is null)
        {
            return null;
        }

        return $"{scheme}://{uri.Authority}";
    }
    private static IReadOnlyList<string> ExtractSessionKeys(JsonElement payload)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        static void AddIfValid(JsonElement value, List<string> output, HashSet<string> deDup)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var key = value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (deDup.Add(key))
            {
                output.Add(key);
            }
        }

        static void ReadSessionObject(JsonElement obj, List<string> output, HashSet<string> deDup)
        {
            if (obj.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (obj.TryGetProperty("sessionKey", out var sessionKey))
            {
                AddIfValid(sessionKey, output, deDup);
            }

            if (obj.TryGetProperty("key", out var key))
            {
                AddIfValid(key, output, deDup);
            }

            if (obj.TryGetProperty("id", out var id))
            {
                AddIfValid(id, output, deDup);
            }
        }

        if (payload.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in payload.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    AddIfValid(item, result, seen);
                }
                else
                {
                    ReadSessionObject(item, result, seen);
                }
            }

            return result;
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        ReadSessionObject(payload, result, seen);

        foreach (var property in payload.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    AddIfValid(item, result, seen);
                }
                else
                {
                    ReadSessionObject(item, result, seen);
                }
            }
        }

        return result;
    }

    private GatewayConnectAuth? BuildAuth(OpenclawConnectionOptions options)
    {
        var token = string.IsNullOrWhiteSpace(options.Token) ? null : options.Token.Trim();
        var password = string.IsNullOrWhiteSpace(options.Password) ? null : options.Password;

        if (token is null && password is null)
        {
            return null;
        }

        return new GatewayConnectAuth
        {
            Token = token,
            Password = password,
        };
    }

    private async Task<string?> TryWaitForChallengeAsync(CancellationToken cancellationToken)
    {
        var challenge = _challengeTcs;
        if (challenge is null)
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ChallengeWaitTimeout);

        try
        {
            return await challenge.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Some gateway variants do not emit connect.challenge before connect.
            return null;
        }
    }

    private async Task<T?> InvokeMethodAsync<T>(string method, object? methodParams, CancellationToken cancellationToken)
    {
        var payload = await InvokeMethodRawAsync(method, methodParams, cancellationToken);
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }

        return payload.Deserialize<T>(_jsonOptions);
    }

    private async Task<JsonElement> InvokeMethodRawAsync(string method, object? methodParams, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("尚未连接到 OpenClaw 网关。");
        }

        var requestId = Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(requestId, tcs))
        {
            throw new InvalidOperationException("待处理请求入队失败。");
        }

        await SendPayloadAsync(
            new GatewayRequestFrame
            {
                Id = requestId,
                Method = method,
                Params = methodParams,
            },
            cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        using var registration = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(requestId, out var pending))
            {
                pending.TrySetException(new TimeoutException($"'{method}' 请求超时（{RequestTimeout.TotalSeconds:0} 秒）。"));
            }
        });

        try
        {
            return await tcs.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    private async Task SendPayloadAsync(object payload, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("连接通道未处于打开状态。");
        }

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult? result;

                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    frame.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(frame.ToArray());
                HandleInboundMessage(json);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during disconnect.
        }
        catch (WebSocketException ex)
        {
            SetConnectionState(false, $"连接错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            SetConnectionState(false, $"接收错误：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _challengeTcs, null)?.TrySetCanceled();
            FailPending(new InvalidOperationException("在收到响应前连接已关闭。"));
            SetConnectionState(false, "已断开");
        }
    }

    private void HandleInboundMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var frameType = typeProperty.GetString();
        if (string.Equals(frameType, "event", StringComparison.Ordinal))
        {
            HandleEventFrame(root);
            return;
        }

        if (string.Equals(frameType, "res", StringComparison.Ordinal))
        {
            HandleResponseFrame(root);
        }
    }

    private void HandleResponseFrame(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String)
        {
            return;
        }

        if (!root.TryGetProperty("ok", out var okProperty) || okProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }

        var id = idProperty.GetString();
        if (string.IsNullOrWhiteSpace(id) || !_pending.TryRemove(id, out var pending))
        {
            return;
        }

        if (okProperty.ValueKind == JsonValueKind.True)
        {
            var payload = root.TryGetProperty("payload", out var payloadProperty) ? payloadProperty.Clone() : default;
            pending.TrySetResult(payload);
            return;
        }

        var code = "UNAVAILABLE";
        var message = "请求失败";

        if (root.TryGetProperty("error", out var errorProperty) && errorProperty.ValueKind == JsonValueKind.Object)
        {
            if (errorProperty.TryGetProperty("code", out var codeProperty) && codeProperty.ValueKind == JsonValueKind.String)
            {
                code = codeProperty.GetString() ?? code;
            }

            if (errorProperty.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String)
            {
                message = messageProperty.GetString() ?? message;
            }
        }

        pending.TrySetException(new GatewayRequestException(code, message));
    }

    private void HandleEventFrame(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var eventProperty) || eventProperty.ValueKind != JsonValueKind.String)
        {
            return;
        }

        if (root.TryGetProperty("seq", out var seqProperty) && seqProperty.ValueKind == JsonValueKind.Number)
        {
            var seq = seqProperty.GetInt64();
            if (_lastEventSeq.HasValue && seq > _lastEventSeq.Value + 1)
            {
                EventGapDetected?.Invoke(this, new EventGapDetectedEventArgs(_lastEventSeq.Value + 1, seq));
            }

            _lastEventSeq = seq;
        }

        var eventName = eventProperty.GetString();
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        if (string.Equals(eventName, "connect.challenge", StringComparison.Ordinal))
        {
            if (root.TryGetProperty("payload", out var payloadProperty)
                && payloadProperty.ValueKind == JsonValueKind.Object
                && payloadProperty.TryGetProperty("nonce", out var nonceProperty)
                && nonceProperty.ValueKind == JsonValueKind.String)
            {
                var nonce = nonceProperty.GetString();
                if (!string.IsNullOrWhiteSpace(nonce))
                {
                    _challengeTcs?.TrySetResult(nonce);
                }
            }

            return;
        }

        if (!root.TryGetProperty("payload", out var payload))
        {
            return;
        }

        switch (eventName)
        {
            case "chat":
            {
                var parsed = payload.Deserialize<GatewayChatEventPayload>(_jsonOptions);
                if (parsed is not null)
                {
                    ChatEventReceived?.Invoke(this, new ChatEventReceivedEventArgs(parsed));
                }

                break;
            }
            case "agent":
            {
                var parsed = payload.Deserialize<GatewayAgentEventPayload>(_jsonOptions);
                if (parsed is not null)
                {
                    AgentEventReceived?.Invoke(this, new AgentEventReceivedEventArgs(parsed));
                }

                break;
            }
        }
    }

    private void SetConnectionState(bool connected, string message)
    {
        var raiseEvent = false;
        lock (_stateLock)
        {
            if (_connected != connected || !string.Equals(_connectionMessage, message, StringComparison.Ordinal))
            {
                _connected = connected;
                _connectionMessage = message;
                raiseEvent = true;
            }
        }

        if (raiseEvent)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(connected, message));
        }
    }

    private void FailPending(Exception ex)
    {
        foreach (var pending in _pending)
        {
            if (_pending.TryRemove(pending.Key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}



