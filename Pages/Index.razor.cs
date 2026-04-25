using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using OpenclawChat.Models;
using OpenclawChat.Services;

namespace OpenclawChat.Pages;

public partial class Index : ComponentBase, IDisposable
{
    private const int ChatHistoryRenderLimit = 200;
    private const int NearBottomThreshold = 450;
    private const int ToolInlineThreshold = 80;
    private const int PreviewMaxLines = 2;
    private const int PreviewMaxChars = 100;
    private const int ToolStreamLimit = 50;
    private const int ToolOutputCharLimit = 120_000;
    private const int CompactionToastDurationMs = 5_000;
    private const int FallbackToastDurationMs = 8_000;
    private static readonly TimeSpan NonStreamingHistoryTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NonStreamingHistoryPollInterval = TimeSpan.FromMilliseconds(800);

    private static readonly Regex SilentReplyRegex = new("^\\s*NO_REPLY\\s*$", RegexOptions.Compiled);
    private static readonly Regex ThinkingTagRegex = new(
        "<\\s*think(?:ing)?\\s*>([\\s\\S]*?)<\\s*/\\s*think(?:ing)?\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Inject] private OpenclawWsClient ChatClient { get; set; } = default!;

    [Inject] private IOptionsSnapshot<OpenclawConnectionOptions> ConnectionOptions { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private OpenclawConnectionOptions _connection = new();
    private readonly List<JsonObject> _chatMessages = [];
    private readonly List<JsonObject> _chatToolMessages = [];
    private readonly List<QueuedMessage> _chatQueue = [];
    private readonly Dictionary<string, ToolStreamEntry> _toolStreamById = [];
    private readonly List<string> _toolStreamOrder = [];
    private readonly HashSet<string> _refreshSessionsAfterChat = [];
    private readonly List<ChatAttachment> _attachments = [];
    private readonly List<string> _sessionKeys = ["main"];
    private bool _loadingSessions;
    private int _requestRoundCounter;
    private int? _activeWaitingRound;
    private bool _waitingForAssistantReply;



    private string _status = "已断开";
    private string? _error;
    private bool _connected;
    private bool _connecting;
    private bool _chatLoading;
    private bool _chatSending;
    private string _chatMessage = string.Empty;
    private string? _chatRunId;
    private string? _chatStream;
    private long? _chatStreamStartedAt;
    private string? _chatThinkingLevel;
    private bool _showThinking = true;
    private bool _showToolStream = true;
    private bool _useStreaming = true;
    private readonly bool _useGatewayEventStreaming = false;

    private bool _chatManualRefreshInFlight;
    private bool _chatHasAutoScrolled;
    private bool _chatUserNearBottom = true;
    private bool _chatNewMessagesBelow;
    private bool _pendingScroll;
    private bool _pendingScrollForce;
    private bool _pendingScrollSmooth;
    private ElementReference _chatThreadRef;
    private ElementReference _chatComposerRef;
    private ElementReference _chatSubmitButtonRef;
    private bool _pendingComposerFocus;

    private bool _sidebarOpen;
    private string? _sidebarContent;

    private CompactionIndicatorStatus? _compactionStatus;
    private FallbackIndicatorStatus? _fallbackStatus;
    private CancellationTokenSource? _compactionToastCts;
    private CancellationTokenSource? _fallbackToastCts;

    private bool CanAbort => !string.IsNullOrWhiteSpace(_chatRunId);

    private bool IsBusy => _chatSending || _chatStream is not null;

    private string WaitingRoundLabel => _activeWaitingRound.HasValue
        ? $"正在等待第 {_activeWaitingRound.Value} 轮回复..."
        : "正在等待回复...";

    protected override void OnInitialized()
    {
        _connection = new OpenclawConnectionOptions
        {
            Endpoint = ConnectionOptions.Value.Endpoint,
            Token = ConnectionOptions.Value.Token,
            Password = ConnectionOptions.Value.Password,
            Origin = ConnectionOptions.Value.Origin,
            SessionKey = string.IsNullOrWhiteSpace(ConnectionOptions.Value.SessionKey)
                ? "main"
                : ConnectionOptions.Value.SessionKey,
        };

        EnsureSessionKeyOption(_connection.SessionKey);

        ChatClient.ConnectionStateChanged += OnConnectionStateChanged;
        ChatClient.EventGapDetected += OnEventGapDetected;
        ChatClient.ChatEventReceived += OnChatEventReceived;
        ChatClient.AgentEventReceived += OnAgentEventReceived;

        _connected = ChatClient.IsConnected;
        _status = _connected ? "已连接" : "已断开";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                await JS.InvokeVoidAsync("openclawChat.bindComposerSubmit", _chatComposerRef, _chatSubmitButtonRef);
            }
            catch (JSException)
            {
                // The browser may still have an older chatInterop.js cached.
            }
        }

        if (_pendingScroll)
        {
            var force = _pendingScrollForce;
            var smooth = _pendingScrollSmooth;
            _pendingScroll = false;
            _pendingScrollForce = false;
            _pendingScrollSmooth = false;
            await PerformAutoScrollAsync(force, smooth);
        }

        if (_pendingComposerFocus)
        {
            _pendingComposerFocus = false;
            await FocusComposerAsync();
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    private async Task ConnectAsync()
    {
        _connecting = true;
        _error = null;

        if (string.IsNullOrWhiteSpace(_connection.SessionKey))
        {
            _connection.SessionKey = "main";
        }

        try
        {
            await ChatClient.ConnectAsync(_connection);
            _connected = true;
            EnsureSessionKeyOption(_connection.SessionKey);
            await LoadSessionKeysAsync();
            ResetChatScroll();
            await LoadHistoryAsync();
            ScheduleAutoScroll(force: true);
        }
        catch (Exception ex)
        {
            _connected = false;
            _error = ex.Message;
        }
        finally
        {
            _connecting = false;
        }
    }

    private async Task DisconnectAsync()
    {
        _error = null;

        try
        {
            await ChatClient.DisconnectAsync();
            _connected = false;
            _chatRunId = null;
            _chatStream = null;
            _chatStreamStartedAt = null;
            _waitingForAssistantReply = false;
            _activeWaitingRound = null;
            ResetToolStream();
            _loadingSessions = false;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private async Task ManualRefreshAsync()
    {
        if (!_connected)
        {
            return;
        }

        _chatManualRefreshInFlight = true;
        _error = null;

        try
        {
            ResetToolStream();
            await LoadHistoryAsync();
        }
        finally
        {
            _chatManualRefreshInFlight = false;
        }
    }

    private async Task OnSessionChangedAsync(ChangeEventArgs args)
    {
        var selected = args.Value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        EnsureSessionKeyOption(selected);

        if (string.Equals(_connection.SessionKey, selected, StringComparison.Ordinal))
        {
            return;
        }

        _connection.SessionKey = selected;

        if (_connected)
        {
            ResetToolStream();
            ResetChatScroll();
            await LoadHistoryAsync();
        }
    }

    private async Task RefreshSessionsAsync()
    {
        if (!_connected)
        {
            return;
        }

        await LoadSessionKeysAsync();
    }

    private async Task LoadSessionKeysAsync()
    {
        if (!_connected)
        {
            return;
        }

        _loadingSessions = true;
        try
        {
            var keys = await ChatClient.ListSessionKeysAsync();
            foreach (var key in keys)
            {
                EnsureSessionKeyOption(key);
            }

            EnsureSessionKeyOption(_connection.SessionKey);
        }
        catch (Exception ex)
        {
            _error = $"加载会话失败：{ex.Message}";
        }
        finally
        {
            _loadingSessions = false;
        }
    }

    private void EnsureSessionKeyOption(string? sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            return;
        }

        var normalized = sessionKey.Trim();
        if (_sessionKeys.Any((existing) => string.Equals(existing, normalized, StringComparison.Ordinal)))
        {
            return;
        }

        _sessionKeys.Add(normalized);
    }

    private async Task LoadHistoryAsync(bool scheduleScroll = true)
    {
        if (!_connected)
        {
            return;
        }

        _chatLoading = true;
        _error = null;

        try
        {
            var response = await ChatClient.GetHistoryAsync(_connection.SessionKey, limit: 200);
            EnsureSessionKeyOption(_connection.SessionKey);
            EnsureSessionKeyOption(response?.SessionKey);
            _chatMessages.Clear();

            var rawMessages = response?.Messages ?? [];
            foreach (var raw in rawMessages)
            {
                var message = ParseObject(raw);
                if (message is null)
                {
                    continue;
                }

                if (IsAssistantSilentReply(message))
                {
                    continue;
                }

                _chatMessages.Add(message);
            }

            _chatThinkingLevel = response?.ThinkingLevel;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _chatLoading = false;
            if (scheduleScroll)
            {
                ScheduleAutoScroll(force: !_chatHasAutoScrolled);
            }
        }
    }

    private async Task SendAsync()
    {
        await SyncComposerValueAsync();
        await HandleSendChatAsync();
    }

    private async Task NewSessionAsync()
    {
        await HandleSendChatAsync("/new", restoreDraft: true);
    }

    private async Task HandleSendChatAsync(string? messageOverride = null, bool restoreDraft = false)
    {
        if (!_connected)
        {
            return;
        }

        var previousDraft = _chatMessage;
        var message = (messageOverride ?? _chatMessage).Trim();
        var attachmentsToSend = messageOverride is null ? _attachments.Select(CloneAttachment).ToList() : [];
        var hasAttachments = attachmentsToSend.Count > 0;

        if (string.IsNullOrWhiteSpace(message) && !hasAttachments)
        {
            return;
        }

        if (IsChatStopCommand(message))
        {
            await AbortAsync();
            return;
        }

        var refreshSessions = IsChatResetCommand(message);

        if (messageOverride is null)
        {
            _chatMessage = string.Empty;
            _attachments.Clear();
        }

        if (IsChatRequestBusy())
        {
            EnqueueChatMessage(message, attachmentsToSend, refreshSessions);
            return;
        }

        await SendChatMessageNowAsync(
            message,
            previousDraft: messageOverride is null ? previousDraft : null,
            restoreDraft: messageOverride is not null && restoreDraft,
            attachments: hasAttachments ? attachmentsToSend : null,
            previousAttachments: messageOverride is null ? attachmentsToSend : null,
            restoreAttachments: messageOverride is not null && restoreDraft,
            refreshSessions: refreshSessions);
    }

    private async Task<bool> SendChatMessageNowAsync(
        string message,
        string? previousDraft,
        bool restoreDraft,
        List<ChatAttachment>? attachments,
        List<ChatAttachment>? previousAttachments,
        bool restoreAttachments,
        bool refreshSessions)
    {
        ResetToolStream();
        var runId = await SendChatMessageCoreAsync(message, attachments);
        var ok = !string.IsNullOrWhiteSpace(runId);

        if (!ok && previousDraft is not null)
        {
            _chatMessage = previousDraft;
        }

        if (!ok && previousAttachments is not null)
        {
            _attachments.Clear();
            _attachments.AddRange(previousAttachments.Select(CloneAttachment));
        }

        if (ok && restoreDraft && !string.IsNullOrWhiteSpace(previousDraft))
        {
            _chatMessage = previousDraft;
        }

        if (ok && restoreAttachments && previousAttachments is not null && previousAttachments.Count > 0)
        {
            _attachments.Clear();
            _attachments.AddRange(previousAttachments.Select(CloneAttachment));
        }

        ScheduleAutoScroll();

        if (ok && !IsChatRequestBusy())
        {
            await FlushChatQueueAsync();
        }

        if (ok && refreshSessions && runId is not null)
        {
            _refreshSessionsAfterChat.Add(runId);
        }

        return ok;
    }

    private async Task<string?> SendChatMessageCoreAsync(string message, List<ChatAttachment>? attachments)
    {
        var trimmed = message.Trim();
        var hasAttachments = attachments is { Count: > 0 };

        if (string.IsNullOrWhiteSpace(trimmed) && !hasAttachments)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var contentBlocks = new JsonArray();

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            contentBlocks.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = trimmed,
            });
        }

        if (hasAttachments)
        {
            foreach (var attachment in attachments!)
            {
                contentBlocks.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = attachment.MimeType,
                        ["data"] = attachment.DataUrl,
                    },
                });
            }
        }

        _chatMessages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = contentBlocks,
            ["timestamp"] = now,
        });

        _chatSending = true;
        _error = null;

        var currentRound = Interlocked.Increment(ref _requestRoundCounter);
        _waitingForAssistantReply = true;
        _activeWaitingRound = currentRound;
        await InvokeAsync(StateHasChanged);

        var runId = Guid.NewGuid().ToString("N");
        if (_useGatewayEventStreaming && _useStreaming)
        {
            _chatRunId = runId;
            _chatStream = string.Empty;
            _chatStreamStartedAt = now;
        }
        else
        {
            _chatRunId = null;
            _chatStream = null;
            _chatStreamStartedAt = null;
        }

        var apiAttachments = hasAttachments
            ? attachments!
                .Select((attachment) => new ChatAttachmentPayload
                {
                    Type = "image",
                    MimeType = attachment.MimeType,
                    Content = attachment.Base64Content,
                })
                .ToList()
            : null;

        try
        {
            var ack = await ChatClient.SendChatAsync(
                _connection.SessionKey,
                trimmed,
                runId,
                apiAttachments);

            if (_useGatewayEventStreaming && _useStreaming && !string.IsNullOrWhiteSpace(ack?.RunId))
            {
                _chatRunId = ack.RunId;
            }

            var found = await WaitForAssistantReplyViaHistoryAsync(now, animateText: _useStreaming);
            if (!found)
            {
                _error = null;
                _chatMessages.Add(BuildAssistantTextMessage("暂时没能回复，请稍后再试。"));
            }

            _chatRunId = null;
            _chatStream = null;
            _chatStreamStartedAt = null;


            return _chatRunId ?? runId;
        }
        catch (Exception ex)
        {
            _chatRunId = null;
            _chatStream = null;
            _chatStreamStartedAt = null;
            _error = ex.Message;

            _chatMessages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"错误：{ex.Message}",
                    },
                },
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            return null;
        }
        finally
        {
            _chatSending = false;
            _waitingForAssistantReply = false;
            _activeWaitingRound = null;
            ScheduleComposerFocus();
            await InvokeAsync(StateHasChanged);
        }
    }
    private async Task<bool> WaitForAssistantReplyViaHistoryAsync(long requestStartedAt, bool animateText)
    {
        var deadline = DateTimeOffset.UtcNow + NonStreamingHistoryTimeout;
        var requestLowerBound = requestStartedAt - 120_000;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ChatHistoryResponse? response;
            try
            {
                response = await ChatClient.GetHistoryAsync(_connection.SessionKey, limit: 200);
            }
            catch
            {
                response = null;
            }

            if (response is not null)
            {
                _chatThinkingLevel = response.ThinkingLevel;

                JsonObject? candidate = null;
                var candidateTs = long.MinValue;
                foreach (var raw in response.Messages)
                {
                    var message = ParseObject(raw);
                    if (message is null)
                    {
                        continue;
                    }

                    if (!string.Equals(GetRole(message), "assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var timestamp = NormalizeTimestamp(GetLong(message, "timestamp"));
                    if (timestamp < requestLowerBound)
                    {
                        continue;
                    }

                    if (IsAssistantSilentReply(message))
                    {
                        continue;
                    }

                    var visibleText = ExtractText(message);
                    if (string.IsNullOrWhiteSpace(visibleText))
                    {
                        continue;
                    }

                    if (timestamp >= candidateTs)
                    {
                        candidate = message;
                        candidateTs = timestamp;
                    }
                }

                if (candidate is not null)
                {
                    if (ContainsEquivalentAssistantMessage(candidate))
                    {
                        // Still seeing a previously rendered assistant message; keep polling for a new one.
                        await Task.Delay(NonStreamingHistoryPollInterval);
                        continue;
                    }

                    if (animateText)
                    {
                        await AnimateAssistantMessageAsync(candidate);
                    }
                    else
                    {
                        _chatMessages.Add(candidate);
                    }

                    return true;
                }
            }

            await Task.Delay(NonStreamingHistoryPollInterval);
        }

        return false;
    }

    private async Task AnimateAssistantMessageAsync(JsonObject message)
    {
        var fullText = ExtractText(message);
        if (string.IsNullOrWhiteSpace(fullText))
        {
            _chatMessages.Add(message);
            return;
        }

        var startedAt = NormalizeTimestamp(GetLong(message, "timestamp"));
        var totalLength = fullText.Length;
        var frames = Math.Clamp(totalLength, 12, 90);
        var step = Math.Max(1, totalLength / frames);
        var delayMs = totalLength switch
        {
            > 2000 => 10,
            > 800 => 14,
            > 200 => 20,
            _ => 26,
        };

        _chatStreamStartedAt = startedAt;
        _chatStream = string.Empty;
        ScheduleAutoScroll();
        StateHasChanged();

        for (var i = 1; i <= totalLength; i += step)
        {
            var length = Math.Min(i, totalLength);
            _chatStream = fullText[..length];
            ScheduleAutoScroll();
            StateHasChanged();
            await Task.Delay(delayMs);
        }

        if (!string.Equals(_chatStream, fullText, StringComparison.Ordinal))
        {
            _chatStream = fullText;
            ScheduleAutoScroll();
            StateHasChanged();
            await Task.Delay(40);
        }

        _chatStream = null;
        _chatStreamStartedAt = null;
        _chatMessages.Add(message);
    }

    private bool ContainsEquivalentAssistantMessage(JsonObject candidate)
    {
        var candidateText = ExtractText(candidate) ?? string.Empty;
        var candidateTs = NormalizeTimestamp(GetLong(candidate, "timestamp"));

        foreach (var existing in _chatMessages)
        {
            if (!string.Equals(GetRole(existing), "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingText = ExtractText(existing) ?? string.Empty;
            var existingTs = NormalizeTimestamp(GetLong(existing, "timestamp"));
            if (candidateTs == existingTs && string.Equals(existingText, candidateText, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
    private async Task AbortAsync()
    {
        if (!_connected)
        {
            return;
        }

        _error = null;

        try
        {
            await ChatClient.AbortChatAsync(_connection.SessionKey, _chatRunId);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private void RemoveQueuedMessage(string id)
    {
        _chatQueue.RemoveAll((queued) => queued.Id == id);
    }

    private void EnqueueChatMessage(
        string text,
        IReadOnlyList<ChatAttachment>? attachments,
        bool refreshSessions)
    {
        var trimmed = text.Trim();
        var hasAttachments = attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(trimmed) && !hasAttachments)
        {
            return;
        }

        _chatQueue.Add(new QueuedMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = trimmed,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attachments = hasAttachments ? attachments!.Select(CloneAttachment).ToList() : null,
            RefreshSessions = refreshSessions,
        });
    }

    private async Task FlushChatQueueAsync()
    {
        if (!_connected || IsChatRequestBusy())
        {
            return;
        }

        if (_chatQueue.Count == 0)
        {
            return;
        }

        var next = _chatQueue[0];
        _chatQueue.RemoveAt(0);

        var ok = await SendChatMessageNowAsync(
            next.Text,
            previousDraft: null,
            restoreDraft: false,
            attachments: next.Attachments?.Select(CloneAttachment).ToList(),
            previousAttachments: null,
            restoreAttachments: false,
            refreshSessions: next.RefreshSessions);

        if (!ok)
        {
            _chatQueue.Insert(0, next);
        }
    }

    private bool IsChatRequestBusy()
    {
        return _chatSending || !string.IsNullOrWhiteSpace(_chatRunId);
    }

    private static bool IsChatStopCommand(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        return normalized is "/stop" or "stop" or "esc" or "abort" or "wait" or "exit";
    }

    private static bool IsChatResetCommand(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        if (normalized is "/new" or "/reset")
        {
            return true;
        }

        return normalized.StartsWith("/new ", StringComparison.Ordinal)
            || normalized.StartsWith("/reset ", StringComparison.Ordinal);
    }

    private async Task OnAttachmentsSelectedAsync(InputFileChangeEventArgs args)
    {
        var files = args.GetMultipleFiles(6);
        foreach (var file in files)
        {
            if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var stream = file.OpenReadStream(5_000_000);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);

            var bytes = memory.ToArray();
            var base64 = Convert.ToBase64String(bytes);
            var dataUrl = $"data:{file.ContentType};base64,{base64}";

            _attachments.Add(new ChatAttachment
            {
                Id = Guid.NewGuid().ToString("N"),
                FileName = file.Name,
                MimeType = file.ContentType,
                Base64Content = base64,
                DataUrl = dataUrl,
            });
        }
    }

    private void RemoveAttachment(string attachmentId)
    {
        _attachments.RemoveAll((attachment) => attachment.Id == attachmentId);
    }

    private string GetComposerPlaceholder()
    {
        if (!_connected)
        {
            return "请先连接网关后再开始聊天...";
        }

        if (_attachments.Count > 0)
        {
            return "请输入消息或继续选择图片...";
        }

        return "输入消息（Enter 发送，Shift+Enter 换行）";
    }

    private void CopyToDraft(string content)
    {
        _chatMessage = content;
    }

    private async Task OnChatScroll()
    {
        var metrics = await GetScrollMetricsAsync();
        if (metrics is null)
        {
            return;
        }

        _chatUserNearBottom = metrics.DistanceFromBottom < NearBottomThreshold;
        if (_chatUserNearBottom)
        {
            _chatNewMessagesBelow = false;
        }
    }

    private async Task ScrollToBottomAsync()
    {
        ResetChatScroll();
        ScheduleAutoScroll(force: true, smooth: true);
        await Task.CompletedTask;
    }

    private void ResetChatScroll()
    {
        _chatHasAutoScrolled = false;
        _chatUserNearBottom = true;
        _chatNewMessagesBelow = false;
    }

    private void ScheduleAutoScroll(bool force = false, bool smooth = false)
    {
        _pendingScroll = true;
        _pendingScrollForce = _pendingScrollForce || force;
        _pendingScrollSmooth = _pendingScrollSmooth || smooth;
    }

    private void ScheduleComposerFocus()
    {
        if (!_connected)
        {
            return;
        }

        _pendingComposerFocus = true;
    }

    private async Task FocusComposerAsync()
    {
        if (!_connected)
        {
            return;
        }

        try
        {
            await _chatComposerRef.FocusAsync(preventScroll: true);
        }
        catch
        {
            // The composer can be unavailable during reconnects or page teardown.
        }
    }

    private async Task SyncComposerValueAsync()
    {
        try
        {
            _chatMessage = await JS.InvokeAsync<string>("openclawChat.getValue", _chatComposerRef);
        }
        catch (JSException)
        {
            // Keep the current bound value if the browser-side helper is unavailable.
        }
    }

    private async Task PerformAutoScrollAsync(bool force, bool smooth)
    {
        var metrics = await GetScrollMetricsAsync();
        if (metrics is null)
        {
            return;
        }

        var effectiveForce = force && !_chatHasAutoScrolled;
        var shouldStick =
            effectiveForce ||
            _chatUserNearBottom ||
            metrics.DistanceFromBottom < NearBottomThreshold;

        if (!shouldStick)
        {
            _chatNewMessagesBelow = true;
            return;
        }

        if (effectiveForce)
        {
            _chatHasAutoScrolled = true;
        }

        await JS.InvokeVoidAsync("openclawChat.scrollToBottom", _chatThreadRef, smooth);
        _chatUserNearBottom = true;
        _chatNewMessagesBelow = false;
    }

    private async Task<ChatScrollMetrics?> GetScrollMetricsAsync()
    {
        try
        {
            return await JS.InvokeAsync<ChatScrollMetrics>("openclawChat.getMetrics", _chatThreadRef);
        }
        catch
        {
            return null;
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        _ = InvokeAsync(() =>
        {
            _connected = args.Connected;
            _status = args.Message;
            if (!args.Connected)
            {
                _chatRunId = null;
                _chatStream = null;
                _chatStreamStartedAt = null;
                _waitingForAssistantReply = false;
                _activeWaitingRound = null;
                _loadingSessions = false;
                ResetToolStream();
            }

            StateHasChanged();
        });
    }

    private void OnEventGapDetected(object? sender, EventGapDetectedEventArgs args)
    {
        _ = InvokeAsync(() =>
        {
            _error = $"检测到事件序号缺口（期望 {args.Expected}，实际 {args.Received}），建议刷新";
            StateHasChanged();
        });
    }

    private void OnChatEventReceived(object? sender, ChatEventReceivedEventArgs args)
    {
        _ = InvokeAsync(async () =>
        {
            if (!_useGatewayEventStreaming)
            {
                return;
            }

            var state = HandleChatEvent(args.Payload);

            if (state is "final" or "error" or "aborted")
            {
                ResetToolStream();
                await FlushChatQueueAsync();
                ScheduleComposerFocus();

                var runId = args.Payload.RunId;
                if (!string.IsNullOrWhiteSpace(runId) && _refreshSessionsAfterChat.Contains(runId))
                {
                    _refreshSessionsAfterChat.Remove(runId);
                }
            }

            if (state == "final" && ShouldReloadHistoryForFinalEvent(args.Payload))
            {
                await LoadHistoryAsync(scheduleScroll: false);
            }

            ScheduleAutoScroll();
            StateHasChanged();
        });
    }

    private void OnAgentEventReceived(object? sender, AgentEventReceivedEventArgs args)
    {
        _ = InvokeAsync(() =>
        {
            HandleAgentEvent(args.Payload);
            StateHasChanged();
        });
    }

    private string? HandleChatEvent(GatewayChatEventPayload payload)
    {
        if (payload is null)
        {
            return null;
        }

        var payloadSession = string.IsNullOrWhiteSpace(payload.SessionKey) ? null : payload.SessionKey;
        if (!string.IsNullOrWhiteSpace(payloadSession)
            && !string.Equals(payloadSession, _connection.SessionKey, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(payloadSession) && string.IsNullOrWhiteSpace(_chatRunId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(payload.RunId)
            && !string.IsNullOrWhiteSpace(_chatRunId)
            && !string.Equals(payload.RunId, _chatRunId, StringComparison.Ordinal)
            && !TryAdoptIncomingRunId(payload))
        {
            return null;
        }

        if (payload.State == "delta")
        {
            var next = ExtractText(payload.Message);
            if (!string.IsNullOrWhiteSpace(next) && !IsSilentReply(next))
            {
                var current = _chatStream ?? string.Empty;
                if (current.Length == 0 || next.Length >= current.Length)
                {
                    _chatStream = next;
                }
            }

            return "delta";
        }

        if (payload.State == "final")
        {
            var finalMessage = NormalizeFinalAssistantMessage(payload.Message);
            if (finalMessage is not null && !IsAssistantSilentReply(finalMessage))
            {
                _chatMessages.Add(finalMessage);
            }
            else if (!string.IsNullOrWhiteSpace(_chatStream) && !IsSilentReply(_chatStream))
            {
                _chatMessages.Add(BuildAssistantTextMessage(_chatStream));
            }

            _chatStream = null;
            _chatRunId = null;
            _chatStreamStartedAt = null;
            return "final";
        }

        if (payload.State == "aborted")
        {
            var normalizedMessage = NormalizeAbortedAssistantMessage(payload.Message);
            if (normalizedMessage is not null && !IsAssistantSilentReply(normalizedMessage))
            {
                _chatMessages.Add(normalizedMessage);
            }
            else if (!string.IsNullOrWhiteSpace(_chatStream) && !IsSilentReply(_chatStream))
            {
                _chatMessages.Add(BuildAssistantTextMessage(_chatStream));
            }

            _chatStream = null;
            _chatRunId = null;
            _chatStreamStartedAt = null;
            return "aborted";
        }

        if (payload.State == "error")
        {
            _chatStream = null;
            _chatRunId = null;
            _chatStreamStartedAt = null;
            _error = payload.ErrorMessage ?? "聊天错误";
            return "error";
        }

        return null;
    }

    private bool TryAdoptIncomingRunId(GatewayChatEventPayload payload)
    {
        if (payload is null
            || string.IsNullOrWhiteSpace(payload.RunId)
            || string.IsNullOrWhiteSpace(_chatRunId))
        {
            return false;
        }

        if (string.Equals(payload.RunId, _chatRunId, StringComparison.Ordinal))
        {
            return false;
        }

        if (_chatStream is null || !string.IsNullOrWhiteSpace(_chatStream))
        {
            return false;
        }

        if (payload.State is not ("delta" or "final" or "aborted" or "error"))
        {
            return false;
        }

        _chatRunId = payload.RunId;
        return true;
    }

    private static bool ShouldReloadHistoryForFinalEvent(GatewayChatEventPayload payload)
    {
        if (payload is null || payload.State != "final")
        {
            return false;
        }

        if (!payload.Message.HasValue)
        {
            return true;
        }

        var message = payload.Message.Value;
        if (message.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (!message.TryGetProperty("role", out var roleProperty) || roleProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var role = roleProperty.GetString()?.Trim().ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(role) && role != "assistant";
    }

    private void HandleAgentEvent(GatewayAgentEventPayload payload)
    {
        if (payload is null)
        {
            return;
        }

        if (payload.Stream == "compaction")
        {
            HandleCompactionEvent(payload);
            return;
        }

        if (payload.Stream is "lifecycle" or "fallback")
        {
            HandleFallbackEvent(payload);
            return;
        }

        if (payload.Stream != "tool")
        {
            return;
        }

        var accepted = ResolveAcceptedSession(payload, allowSessionScopedWhenIdle: false);
        if (!accepted.Accepted)
        {
            return;
        }

        var data = ParseObject(payload.Data) ?? new JsonObject();
        var toolCallId = GetString(data, "toolCallId") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        var name = GetString(data, "name") ?? "tool";
        var phase = GetString(data, "phase") ?? string.Empty;
        JsonNode? args = phase == "start" ? data["args"]?.DeepClone() : null;

        var output = phase switch
        {
            "update" => FormatToolOutput(data["partialResult"]),
            "result" => FormatToolOutput(data["result"]),
            _ => null,
        };

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!_toolStreamById.TryGetValue(toolCallId, out var entry))
        {
            entry = new ToolStreamEntry
            {
                ToolCallId = toolCallId,
                RunId = payload.RunId,
                SessionKey = accepted.SessionKey,
                Name = name,
                Args = args,
                Output = output,
                StartedAt = payload.Ts > 0 ? payload.Ts : now,
                UpdatedAt = now,
                Message = new JsonObject(),
            };

            _toolStreamById[toolCallId] = entry;
            _toolStreamOrder.Add(toolCallId);
        }
        else
        {
            entry.Name = name;
            if (args is not null)
            {
                entry.Args = args;
            }

            if (output is not null)
            {
                entry.Output = output;
            }

            entry.UpdatedAt = now;
        }

        entry.Message = BuildToolStreamMessage(entry);
        TrimToolStream();
        SyncToolStreamMessages();
    }

    private void HandleCompactionEvent(GatewayAgentEventPayload payload)
    {
        var data = ParseObject(payload.Data) ?? new JsonObject();
        var phase = GetString(data, "phase") ?? string.Empty;

        CancelCompactionToastClear();

        if (phase == "start")
        {
            _compactionStatus = new CompactionIndicatorStatus
            {
                Active = true,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CompletedAt = null,
            };
            return;
        }

        if (phase == "end")
        {
            _compactionStatus = new CompactionIndicatorStatus
            {
                Active = false,
                StartedAt = _compactionStatus?.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            _compactionToastCts = new CancellationTokenSource();
            _ = ClearCompactionToastAsync(_compactionToastCts.Token);
        }
    }

    private async Task ClearCompactionToastAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(CompactionToastDurationMs, cancellationToken);
            await InvokeAsync(() =>
            {
                _compactionStatus = null;
                StateHasChanged();
            });
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation.
        }
    }

    private void HandleFallbackEvent(GatewayAgentEventPayload payload)
    {
        var data = ParseObject(payload.Data) ?? new JsonObject();
        var phase = payload.Stream == "fallback" ? "fallback" : GetString(data, "phase");

        if (payload.Stream == "lifecycle" && phase is not ("fallback" or "fallback_cleared"))
        {
            return;
        }

        var accepted = ResolveAcceptedSession(payload, allowSessionScopedWhenIdle: true);
        if (!accepted.Accepted)
        {
            return;
        }

        var selected =
            ResolveModelLabel(GetString(data, "selectedProvider"), GetString(data, "selectedModel"))
            ?? ResolveModelLabel(GetString(data, "fromProvider"), GetString(data, "fromModel"));
        var active =
            ResolveModelLabel(GetString(data, "activeProvider"), GetString(data, "activeModel"))
            ?? ResolveModelLabel(GetString(data, "toProvider"), GetString(data, "toModel"));
        var previous =
            ResolveModelLabel(GetString(data, "previousActiveProvider"), GetString(data, "previousActiveModel"))
            ?? GetString(data, "previousActiveModel");

        if (string.IsNullOrWhiteSpace(selected) || string.IsNullOrWhiteSpace(active))
        {
            return;
        }

        if (phase == "fallback" && string.Equals(selected, active, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var reason = GetString(data, "reasonSummary") ?? GetString(data, "reason");
        var attempts = ParseFallbackAttempts(data);

        CancelFallbackToastClear();

        _fallbackStatus = new FallbackIndicatorStatus
        {
            Phase = phase == "fallback_cleared" ? "cleared" : "active",
            Selected = selected,
            Active = phase == "fallback_cleared" ? selected : active,
            Previous = phase == "fallback_cleared"
                ? previous ?? (active != selected ? active : null)
                : null,
            Reason = reason,
            Attempts = attempts,
            OccurredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        _fallbackToastCts = new CancellationTokenSource();
        _ = ClearFallbackToastAsync(_fallbackToastCts.Token);
    }

    private async Task ClearFallbackToastAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FallbackToastDurationMs, cancellationToken);
            await InvokeAsync(() =>
            {
                _fallbackStatus = null;
                StateHasChanged();
            });
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation.
        }
    }

    private void CancelCompactionToastClear()
    {
        _compactionToastCts?.Cancel();
        _compactionToastCts?.Dispose();
        _compactionToastCts = null;
    }

    private void CancelFallbackToastClear()
    {
        _fallbackToastCts?.Cancel();
        _fallbackToastCts?.Dispose();
        _fallbackToastCts = null;
    }

    private (bool Accepted, string? SessionKey) ResolveAcceptedSession(
        GatewayAgentEventPayload payload,
        bool allowSessionScopedWhenIdle)
    {
        var sessionKey = string.IsNullOrWhiteSpace(payload.SessionKey) ? null : payload.SessionKey;

        if (!string.IsNullOrWhiteSpace(sessionKey)
            && !string.Equals(sessionKey, _connection.SessionKey, StringComparison.Ordinal))
        {
            return (false, null);
        }

        if (string.IsNullOrWhiteSpace(_chatRunId) && allowSessionScopedWhenIdle && !string.IsNullOrWhiteSpace(sessionKey))
        {
            return (true, sessionKey);
        }

        if (string.IsNullOrWhiteSpace(sessionKey)
            && !string.IsNullOrWhiteSpace(_chatRunId)
            && !string.Equals(payload.RunId, _chatRunId, StringComparison.Ordinal))
        {
            return (false, null);
        }

        if (!string.IsNullOrWhiteSpace(_chatRunId)
            && !string.Equals(payload.RunId, _chatRunId, StringComparison.Ordinal))
        {
            return (false, null);
        }

        if (string.IsNullOrWhiteSpace(_chatRunId))
        {
            return (false, null);
        }

        return (true, sessionKey);
    }

    private void TrimToolStream()
    {
        if (_toolStreamOrder.Count <= ToolStreamLimit)
        {
            return;
        }

        var overflow = _toolStreamOrder.Count - ToolStreamLimit;
        for (var i = 0; i < overflow; i++)
        {
            var id = _toolStreamOrder[0];
            _toolStreamOrder.RemoveAt(0);
            _toolStreamById.Remove(id);
        }
    }

    private void SyncToolStreamMessages()
    {
        _chatToolMessages.Clear();
        foreach (var id in _toolStreamOrder)
        {
            if (_toolStreamById.TryGetValue(id, out var entry))
            {
                _chatToolMessages.Add(entry.Message);
            }
        }
    }

    private void ResetToolStream()
    {
        _toolStreamById.Clear();
        _toolStreamOrder.Clear();
        _chatToolMessages.Clear();
        _compactionStatus = null;
        _fallbackStatus = null;
        CancelCompactionToastClear();
        CancelFallbackToastClear();
    }

    private static JsonObject BuildToolStreamMessage(ToolStreamEntry entry)
    {
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "toolcall",
                ["name"] = entry.Name,
                ["arguments"] = entry.Args?.DeepClone() ?? new JsonObject(),
            },
        };

        if (!string.IsNullOrWhiteSpace(entry.Output))
        {
            content.Add(new JsonObject
            {
                ["type"] = "toolresult",
                ["name"] = entry.Name,
                ["text"] = entry.Output,
            });
        }

        return new JsonObject
        {
            ["role"] = "assistant",
            ["toolCallId"] = entry.ToolCallId,
            ["runId"] = entry.RunId,
            ["content"] = content,
            ["timestamp"] = entry.StartedAt,
        };
    }

    private static List<string> ParseFallbackAttempts(JsonObject data)
    {
        if (data["attemptSummaries"] is JsonArray summaries)
        {
            var values = summaries
                .Select((entry) => entry?.GetValue<string>()?.Trim())
                .Where((entry) => !string.IsNullOrWhiteSpace(entry))
                .Cast<string>()
                .ToList();
            if (values.Count > 0)
            {
                return values;
            }
        }

        var attempts = new List<string>();
        if (data["attempts"] is not JsonArray rawAttempts)
        {
            return attempts;
        }

        foreach (var attemptNode in rawAttempts)
        {
            if (attemptNode is not JsonObject attempt)
            {
                continue;
            }

            var provider = GetString(attempt, "provider");
            var model = GetString(attempt, "model");
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            var reason = GetString(attempt, "reason")
                ?? GetString(attempt, "code")
                ?? GetString(attempt, "error")
            ?? "错误";
            var modelRef = ResolveModelLabel(provider, model) ?? $"{provider}/{model}";
            attempts.Add($"{modelRef}: {reason}");
        }

        return attempts;
    }

    private static string? ResolveModelLabel(string? provider, string? model)
    {
        var providerValue = provider?.Trim();
        var modelValue = model?.Trim();

        if (string.IsNullOrWhiteSpace(modelValue))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(providerValue))
        {
            var prefix = providerValue + "/";
            if (modelValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var trimmedModel = modelValue[prefix.Length..].Trim();
                return string.IsNullOrWhiteSpace(trimmedModel)
                    ? null
                    : providerValue + "/" + trimmedModel;
            }

            return providerValue + "/" + modelValue;
        }

        var slash = modelValue.IndexOf('/');
        if (slash > 0)
        {
            var p = modelValue[..slash].Trim();
            var m = modelValue[(slash + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(p) && !string.IsNullOrWhiteSpace(m))
            {
                return p + "/" + m;
            }
        }

        return modelValue;
    }
    private static string? FormatToolOutput(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        string text;
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var stringValue))
            {
                text = stringValue;
            }
            else
            {
                text = jsonValue.ToJsonString();
            }
        }
        else
        {
            var contentText = ExtractToolOutputText(value);
            text = !string.IsNullOrWhiteSpace(contentText)
                ? contentText
                : value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        if (text.Length <= ToolOutputCharLimit)
        {
            return text;
        }

        var truncated = text[..ToolOutputCharLimit];
        return truncated + $"\n\n... 已截断（总长度 {text.Length} 字符，仅展示前 {ToolOutputCharLimit} 字符）。";
    }

    private static string? ExtractToolOutputText(JsonNode value)
    {
        if (value is not JsonObject obj)
        {
            return null;
        }

        if (obj["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (obj["content"] is not JsonArray content)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var item in content)
        {
            if (item is not JsonObject entry)
            {
                continue;
            }

            var type = GetString(entry, "type");
            if (type == "text" && entry["text"] is JsonValue lineValue && lineValue.TryGetValue<string>(out var line))
            {
                lines.Add(line);
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private List<ChatRenderItem> BuildChatItems()
    {
        var items = new List<ChatRenderItem>();
        var historyStart = Math.Max(0, _chatMessages.Count - ChatHistoryRenderLimit);

        if (historyStart > 0)
        {
            var hidden = historyStart;
            items.Add(new ChatMessageItem
            {
                Message = new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"仅显示最近 {ChatHistoryRenderLimit} 条消息（隐藏 {hidden} 条）。",
                        },
                    },
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            });
        }

        for (var i = historyStart; i < _chatMessages.Count; i++)
        {
            var message = _chatMessages[i];

            if (message["__openclaw"] is JsonObject marker
                && string.Equals(GetString(marker, "kind"), "compaction", StringComparison.Ordinal))
            {
                items.Add(new ChatDividerItem
                {
                    Label = "上下文压缩",
                    Timestamp = NormalizeTimestamp(GetLong(message, "timestamp")),
                });
                continue;
            }

            if (!_showToolStream && NormalizeRoleForGrouping(GetRole(message)) == "tool")
            {
                continue;
            }

            items.Add(new ChatMessageItem { Message = message });
        }

        if (_showToolStream)
        {
            foreach (var message in _chatToolMessages)
            {
                items.Add(new ChatMessageItem { Message = message });
            }
        }

        if (_chatStream is not null)
        {
            if (_chatStream.Trim().Length > 0)
            {
                items.Add(new ChatStreamItem
                {
                    Text = _chatStream,
                    StartedAt = _chatStreamStartedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
            }
            else
            {
                items.Add(new ChatReadingIndicatorItem());
            }
        }

        return GroupMessages(items);
    }

    private List<ChatRenderItem> GroupMessages(List<ChatRenderItem> items)
    {
        var grouped = new List<ChatRenderItem>();
        ChatGroupItem? currentGroup = null;

        foreach (var item in items)
        {
            if (item is not ChatMessageItem messageItem)
            {
                if (currentGroup is not null)
                {
                    grouped.Add(currentGroup);
                    currentGroup = null;
                }

                grouped.Add(item);
                continue;
            }

            var role = NormalizeRoleForGrouping(GetRole(messageItem.Message));
            var timestamp = NormalizeTimestamp(GetLong(messageItem.Message, "timestamp"));

            if (currentGroup is null || currentGroup.Role != role)
            {
                if (currentGroup is not null)
                {
                    grouped.Add(currentGroup);
                }

                currentGroup = new ChatGroupItem
                {
                    Role = role,
                    Timestamp = timestamp,
                    IsStreaming = false,
                };
            }

            currentGroup.Messages.Add(messageItem.Message);
        }

        if (currentGroup is not null)
        {
            grouped.Add(currentGroup);
        }

        return grouped;
    }

    private static string GetRoleClass(string role)
    {
        var normalized = NormalizeRoleForGrouping(role);
        return normalized switch
        {
            "user" => "user",
            "assistant" => "assistant",
            "tool" => "tool",
            _ => "other",
        };
    }

    private static string GetAvatar(string role)
    {
        var normalized = NormalizeRoleForGrouping(role);
        return normalized switch
        {
            "user" => "我",
            "assistant" => "AI",
            "tool" => "工",
            _ => "?",
        };
    }

    private static string GetSenderName(string role)
    {
        var normalized = NormalizeRoleForGrouping(role);
        return normalized switch
        {
            "user" => "我",
            "assistant" => "助手",
            _ => normalized,
        };
    }

    private static string NormalizeRoleForGrouping(string role)
    {
        var lower = role.ToLowerInvariant();
        if (lower == "user")
        {
            return "user";
        }

        if (lower == "assistant")
        {
            return "assistant";
        }

        if (lower is "toolresult" or "tool_result" or "tool" or "function")
        {
            return "tool";
        }

        if (lower == "system")
        {
            return "system";
        }

        return role;
    }

    private static string GetRole(JsonObject message)
    {
        return GetString(message, "role") ?? "未知";
    }

    private static bool IsAssistantRole(JsonObject message)
    {
        return string.Equals(GetRole(message), "assistant", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractText(JsonObject message)
    {
        var raw = ExtractRawText(message);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (string.Equals(GetRole(message), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return StripThinkingTags(raw);
        }

        return raw;
    }

    private static string? ExtractText(JsonElement? message)
    {
        if (!message.HasValue || message.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parsed = ParseObject(message.Value);
        return parsed is null ? null : ExtractText(parsed);
    }

    private static string? ExtractRawText(JsonObject message)
    {
        if (message["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var textContent))
        {
            return textContent;
        }

        if (message["content"] is JsonArray content)
        {
            var parts = content
                .OfType<JsonObject>()
                .Where((entry) => string.Equals(GetString(entry, "type"), "text", StringComparison.OrdinalIgnoreCase))
                .Select((entry) => GetString(entry, "text"))
                .Where((text) => !string.IsNullOrWhiteSpace(text))
                .Cast<string>()
                .ToList();

            if (parts.Count > 0)
            {
                return string.Join("\n", parts);
            }
        }

        if (message["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var textField))
        {
            return textField;
        }

        return null;
    }

    private static string? ExtractThinking(JsonObject message)
    {
        if (message["content"] is JsonArray content)
        {
            var parts = content
                .OfType<JsonObject>()
                .Where((entry) => string.Equals(GetString(entry, "type"), "thinking", StringComparison.OrdinalIgnoreCase))
                .Select((entry) => GetString(entry, "thinking")?.Trim())
                .Where((value) => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
            if (parts.Count > 0)
            {
                return string.Join("\n", parts);
            }
        }

        var raw = ExtractRawText(message);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var matches = ThinkingTagRegex.Matches(raw);
        if (matches.Count == 0)
        {
            return null;
        }

        var extracted = matches
            .Select((match) => match.Groups[1].Value.Trim())
            .Where((value) => !string.IsNullOrWhiteSpace(value))
            .ToList();

        return extracted.Count > 0 ? string.Join("\n", extracted) : null;
    }

    private static string StripThinkingTags(string text)
    {
        return ThinkingTagRegex.Replace(text, string.Empty).Trim();
    }

    private static bool IsSilentReply(string text)
    {
        return SilentReplyRegex.IsMatch(text);
    }

    private static bool IsAssistantSilentReply(JsonObject message)
    {
        var role = GetRole(message);
        if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (message["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var fieldText))
        {
            return IsSilentReply(fieldText);
        }

        var text = ExtractText(message);
        return text is not null && IsSilentReply(text);
    }

    private static JsonObject? NormalizeFinalAssistantMessage(JsonElement? message)
    {
        if (!message.HasValue || message.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parsed = ParseObject(message.Value);
        if (parsed is null)
        {
            return null;
        }

        var role = GetString(parsed, "role");
        if (!string.IsNullOrWhiteSpace(role)
            && !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hasContent = parsed["content"] is not null;
        var hasText = parsed["text"] is JsonValue;

        if (!hasContent && !hasText)
        {
            return null;
        }

        return parsed;
    }

    private static JsonObject? NormalizeAbortedAssistantMessage(JsonElement? message)
    {
        if (!message.HasValue || message.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parsed = ParseObject(message.Value);
        if (parsed is null)
        {
            return null;
        }

        var role = GetString(parsed, "role");
        if (!string.Equals(role, "assistant", StringComparison.Ordinal))
        {
            return null;
        }

        if (parsed["content"] is not JsonArray)
        {
            return null;
        }

        return parsed;
    }

    private static JsonObject BuildAssistantTextMessage(string text)
    {
        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }
    private static List<string> ExtractImages(JsonObject message)
    {
        var images = new List<string>();

        if (message["content"] is not JsonArray content)
        {
            return images;
        }

        foreach (var entryNode in content)
        {
            if (entryNode is not JsonObject entry)
            {
                continue;
            }

            var type = GetString(entry, "type")?.ToLowerInvariant();
            if (type == "image")
            {
                if (entry["source"] is JsonObject source)
                {
                    var sourceType = GetString(source, "type");
                    var data = GetString(source, "data");
                    var mediaType = GetString(source, "media_type") ?? "image/png";

                    if (sourceType == "base64" && !string.IsNullOrWhiteSpace(data))
                    {
                        images.Add(data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                            ? data
                            : $"data:{mediaType};base64,{data}");
                        continue;
                    }
                }

                var url = GetString(entry, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    images.Add(url);
                }
            }
            else if (type == "image_url" && entry["image_url"] is JsonObject imageUrl)
            {
                var url = GetString(imageUrl, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    images.Add(url);
                }
            }
        }

        return images;
    }

    private static List<ToolCard> ExtractToolCards(JsonObject message)
    {
        var cards = new List<ToolCard>();
        var content = message["content"] as JsonArray;

        if (content is not null)
        {
            foreach (var entryNode in content.OfType<JsonObject>())
            {
                var type = GetString(entryNode, "type")?.ToLowerInvariant() ?? string.Empty;
                var isToolCall =
                    type is "toolcall" or "tool_call" or "tooluse" or "tool_use"
                    || (!string.IsNullOrWhiteSpace(GetString(entryNode, "name")) && entryNode["arguments"] is not null);

                if (!isToolCall)
                {
                    continue;
                }

                cards.Add(new ToolCard
                {
                    Kind = ToolCardKind.Call,
                    Name = GetString(entryNode, "name") ?? "tool",
                    Args = (entryNode["arguments"] ?? entryNode["args"])?.DeepClone(),
                });
            }

            foreach (var entryNode in content.OfType<JsonObject>())
            {
                var type = GetString(entryNode, "type")?.ToLowerInvariant() ?? string.Empty;
                if (type is not ("toolresult" or "tool_result"))
                {
                    continue;
                }

                cards.Add(new ToolCard
                {
                    Kind = ToolCardKind.Result,
                    Name = GetString(entryNode, "name") ?? "tool",
                    Text = GetString(entryNode, "text") ?? GetString(entryNode, "content"),
                });
            }
        }

        if (IsToolResultMessage(message) && cards.All((card) => card.Kind != ToolCardKind.Result))
        {
            cards.Add(new ToolCard
            {
                Kind = ToolCardKind.Result,
                Name = GetString(message, "toolName") ?? GetString(message, "tool_name") ?? "tool",
                Text = ExtractText(message),
            });
        }

        return cards;
    }

    private static bool IsToolResultMessage(JsonObject message)
    {
        var role = GetRole(message).ToLowerInvariant();
        if (role is "toolresult" or "tool_result")
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(GetString(message, "toolCallId"))
            || !string.IsNullOrWhiteSpace(GetString(message, "tool_call_id")))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(GetString(message, "toolName"))
            || !string.IsNullOrWhiteSpace(GetString(message, "tool_name")))
        {
            return true;
        }

        if (message["content"] is not JsonArray content)
        {
            return false;
        }

        return content
            .OfType<JsonObject>()
            .Any((entry) =>
            {
                var type = GetString(entry, "type")?.ToLowerInvariant();
                return type is "toolresult" or "tool_result";
            });
    }

    private static string? FormatToolCardDetail(ToolCard card)
    {
        if (card.Args is null)
        {
            return null;
        }

        if (card.Args is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return LimitText(text.Trim(), 120);
            }

            return LimitText(value.ToJsonString(), 120);
        }

        if (card.Args is JsonObject args)
        {
            var keys = new[]
            {
                "path",
                "url",
                "query",
                "command",
                "to",
                "channelId",
                "messageId",
                "emoji",
                "id",
            };

            foreach (var key in keys)
            {
                var text = GetString(args, key);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return key + ": " + LimitText(text, 120);
                }
            }

            return LimitText(args.ToJsonString(), 120);
        }

        return LimitText(card.Args.ToJsonString(), 120);
    }

    private static string LimitText(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        return value[..max] + "...";
    }

    private static bool IsShortToolOutput(ToolCard card)
    {
        return !string.IsNullOrWhiteSpace(card.Text) && card.Text!.Length <= ToolInlineThreshold;
    }

    private static bool CanOpenToolSidebar(ToolCard card)
    {
        return !string.IsNullOrWhiteSpace(card.Text) || !string.IsNullOrWhiteSpace(FormatToolCardDetail(card));
    }

    private void OpenToolSidebar(ToolCard card)
    {
        if (!CanOpenToolSidebar(card))
        {
            return;
        }

        _sidebarOpen = true;
        if (!string.IsNullOrWhiteSpace(card.Text))
        {
            _sidebarContent = FormatToolOutputForSidebar(card.Text!);
            return;
        }

        var detail = FormatToolCardDetail(card);
        _sidebarContent = string.IsNullOrWhiteSpace(detail)
            ? $"## {card.Name}\n\n无输出，工具已执行完成。"
            : $"## {card.Name}\n\n{detail}\n\n无输出，工具已执行完成。";
    }

    private static string FormatToolOutputForSidebar(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var parsed = JsonDocument.Parse(trimmed);
                return JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true,
                });
            }
            catch
            {
                // Fallback to raw output.
            }
        }

        return text;
    }

    private void CloseSidebar()
    {
        _sidebarOpen = false;
        _sidebarContent = null;
    }

    private static string GetTruncatedPreview(string text)
    {
        var lines = text.Split('\n');
        var picked = string.Join("\n", lines.Take(PreviewMaxLines));

        if (picked.Length > PreviewMaxChars)
        {
            return picked[..PreviewMaxChars] + "...";
        }

        return lines.Length > PreviewMaxLines ? picked + "..." : picked;
    }

    private static string GetQueuedSummary(QueuedMessage queued)
    {
        if (!string.IsNullOrWhiteSpace(queued.Text))
        {
            return queued.Text;
        }

        var count = queued.Attachments?.Count ?? 0;
        return count > 0 ? $"图片（{count}）" : string.Empty;
    }

    private bool ShowCompactionIndicator()
    {
        if (_compactionStatus is null)
        {
            return false;
        }

        if (_compactionStatus.Active)
        {
            return true;
        }

        if (_compactionStatus.CompletedAt is null)
        {
            return false;
        }

        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _compactionStatus.CompletedAt.Value;
        return elapsed < CompactionToastDurationMs;
    }

    private bool IsCompactionActive()
    {
        return _compactionStatus?.Active == true;
    }

    private bool ShowFallbackIndicator()
    {
        if (_fallbackStatus is null)
        {
            return false;
        }

        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _fallbackStatus.OccurredAt;
        return elapsed < FallbackToastDurationMs;
    }

    private bool IsFallbackCleared()
    {
        return _fallbackStatus?.Phase == "cleared";
    }

    private string BuildFallbackDetails()
    {
        if (_fallbackStatus is null)
        {
            return string.Empty;
        }

        var details = new List<string>
        {
            $"已选：{_fallbackStatus.Selected}",
            IsFallbackCleared() ? $"当前：{_fallbackStatus.Selected}" : $"当前：{_fallbackStatus.Active}",
        };

        if (IsFallbackCleared() && !string.IsNullOrWhiteSpace(_fallbackStatus.Previous))
        {
            details.Add($"上一次回退：{_fallbackStatus.Previous}");
        }

        if (!string.IsNullOrWhiteSpace(_fallbackStatus.Reason))
        {
            details.Add($"原因：{_fallbackStatus.Reason}");
        }

        if (_fallbackStatus.Attempts.Count > 0)
        {
            details.Add($"尝试：{string.Join(" | ", _fallbackStatus.Attempts.Take(3))}");
        }

        return string.Join(" • ", details);
    }

    private static string FormatTimestamp(long unixMs)
    {
        var normalized = NormalizeTimestamp(unixMs);
        return DateTimeOffset.FromUnixTimeMilliseconds(normalized).ToLocalTime().ToString("HH:mm");
    }

    private static long NormalizeTimestamp(long? unixMs)
    {
        if (!unixMs.HasValue || unixMs.Value <= 0)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        var value = unixMs.Value;

        // second-based unix timestamp
        if (value < 1_000_000_000_000L)
        {
            return value * 1_000;
        }

        // nanosecond-based unix timestamp
        if (value > 9_999_999_999_999_999L)
        {
            return value / 1_000_000;
        }

        // microsecond-based unix timestamp
        if (value > 9_999_999_999_999L)
        {
            return value / 1_000;
        }

        return value;
    }

    private static long? GetLong(JsonObject obj, string property)
    {
        if (obj[property] is JsonValue value && value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (obj[property] is JsonValue intValue && intValue.TryGetValue<int>(out var intResult))
        {
            return intResult;
        }

        return null;
    }

    private static string? GetString(JsonObject obj, string property)
    {
        if (obj[property] is JsonValue value && value.TryGetValue<string>(out var stringValue))
        {
            var trimmed = stringValue?.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        return null;
    }

    private static JsonObject? ParseObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonNode.Parse(element.GetRawText()) as JsonObject;
    }

    private static ChatAttachment CloneAttachment(ChatAttachment source)
    {
        return new ChatAttachment
        {
            Id = source.Id,
            FileName = source.FileName,
            MimeType = source.MimeType,
            DataUrl = source.DataUrl,
            Base64Content = source.Base64Content,
        };
    }

    public void Dispose()
    {
        ChatClient.ConnectionStateChanged -= OnConnectionStateChanged;
        ChatClient.EventGapDetected -= OnEventGapDetected;
        ChatClient.ChatEventReceived -= OnChatEventReceived;
        ChatClient.AgentEventReceived -= OnAgentEventReceived;

        CancelCompactionToastClear();
        CancelFallbackToastClear();
    }

    private sealed class ChatScrollMetrics
    {
        public double DistanceFromBottom { get; set; }
    }

    private abstract class ChatRenderItem
    {
    }

    private sealed class ChatMessageItem : ChatRenderItem
    {
        public required JsonObject Message { get; set; }
    }

    private sealed class ChatDividerItem : ChatRenderItem
    {
        public string Label { get; set; } = "上下文压缩";

        public long Timestamp { get; set; }
    }

    private sealed class ChatStreamItem : ChatRenderItem
    {
        public string Text { get; set; } = string.Empty;

        public long StartedAt { get; set; }
    }

    private sealed class ChatReadingIndicatorItem : ChatRenderItem
    {
    }

    private sealed class ChatGroupItem : ChatRenderItem
    {
        public string Role { get; set; } = "assistant";

        public List<JsonObject> Messages { get; } = [];

        public long Timestamp { get; set; }

        public bool IsStreaming { get; set; }
    }

    private sealed class QueuedMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Text { get; set; } = string.Empty;

        public long CreatedAt { get; set; }

        public List<ChatAttachment>? Attachments { get; set; }

        public bool RefreshSessions { get; set; }
    }

    private sealed class ToolStreamEntry
    {
        public string ToolCallId { get; set; } = string.Empty;

        public string RunId { get; set; } = string.Empty;

        public string? SessionKey { get; set; }

        public string Name { get; set; } = "tool";

        public JsonNode? Args { get; set; }

        public string? Output { get; set; }

        public long StartedAt { get; set; }

        public long UpdatedAt { get; set; }

        public JsonObject Message { get; set; } = new();
    }

    private sealed class CompactionIndicatorStatus
    {
        public bool Active { get; set; }

        public long? StartedAt { get; set; }

        public long? CompletedAt { get; set; }
    }

    private sealed class FallbackIndicatorStatus
    {
        public string Phase { get; set; } = "active";

        public string Selected { get; set; } = string.Empty;

        public string Active { get; set; } = string.Empty;

        public string? Previous { get; set; }

        public string? Reason { get; set; }

        public List<string> Attempts { get; set; } = [];

        public long OccurredAt { get; set; }
    }

    private enum ToolCardKind
    {
        Call,
        Result,
    }

    private sealed class ToolCard
    {
        public ToolCardKind Kind { get; set; }

        public string Name { get; set; } = "tool";

        public JsonNode? Args { get; set; }

        public string? Text { get; set; }
    }
}


