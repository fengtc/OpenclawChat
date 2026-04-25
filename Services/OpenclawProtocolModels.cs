using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenclawChat.Services;

public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(bool connected, string message)
    {
        Connected = connected;
        Message = message;
    }

    public bool Connected { get; }

    public string Message { get; }
}

public sealed class EventGapDetectedEventArgs : EventArgs
{
    public EventGapDetectedEventArgs(long expected, long received)
    {
        Expected = expected;
        Received = received;
    }

    public long Expected { get; }

    public long Received { get; }
}

public sealed class ChatEventReceivedEventArgs : EventArgs
{
    public ChatEventReceivedEventArgs(GatewayChatEventPayload payload)
    {
        Payload = payload;
    }

    public GatewayChatEventPayload Payload { get; }
}

public sealed class AgentEventReceivedEventArgs : EventArgs
{
    public AgentEventReceivedEventArgs(GatewayAgentEventPayload payload)
    {
        Payload = payload;
    }

    public GatewayAgentEventPayload Payload { get; }
}

public sealed class GatewayChatEventPayload
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("sessionKey")]
    public string? SessionKey { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public JsonElement? Message { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("stopReason")]
    public string? StopReason { get; set; }
}

public sealed class GatewayAgentEventPayload
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("stream")]
    public string Stream { get; set; } = string.Empty;

    [JsonPropertyName("ts")]
    public long Ts { get; set; }

    [JsonPropertyName("sessionKey")]
    public string? SessionKey { get; set; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }
}

public sealed class GatewayHelloOk
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("protocol")]
    public int Protocol { get; set; }

    [JsonPropertyName("server")]
    public GatewayHelloServer? Server { get; set; }
}

public sealed class GatewayHelloServer
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("connId")]
    public string? ConnId { get; set; }
}

public sealed class ChatHistoryResponse
{
    [JsonPropertyName("sessionKey")]
    public string? SessionKey { get; set; }

    [JsonPropertyName("messages")]
    public List<JsonElement> Messages { get; set; } = [];

    [JsonPropertyName("thinkingLevel")]
    public string? ThinkingLevel { get; set; }
}

public sealed class ChatSendAck
{
    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class ChatAbortAck
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("aborted")]
    public bool Aborted { get; set; }

    [JsonPropertyName("runIds")]
    public List<string>? RunIds { get; set; }
}

public sealed class GatewayRequestException : Exception
{
    public GatewayRequestException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed class GatewayRequestFrame
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "req";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

internal sealed class GatewayConnectParams
{
    [JsonPropertyName("minProtocol")]
    public int MinProtocol { get; set; } = 3;

    [JsonPropertyName("maxProtocol")]
    public int MaxProtocol { get; set; } = 3;

    [JsonPropertyName("client")]
    public GatewayConnectClient Client { get; set; } = new();

    [JsonPropertyName("caps")]
    public List<string>? Caps { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "operator";

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = ["operator.admin", "operator.approvals", "operator.pairing"];

    [JsonPropertyName("auth")]
    public GatewayConnectAuth? Auth { get; set; }

    [JsonPropertyName("userAgent")]
    public string UserAgent { get; set; } = "blazor-server";

    [JsonPropertyName("locale")]
    public string Locale { get; set; } = "zh-CN";


}

internal sealed class GatewayConnectClient
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "openclaw-control-ui";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "blazor-webchat";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "web";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "webchat";

    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
}

internal sealed class GatewayConnectAuth
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }
}

public sealed class ChatAttachmentPayload
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "image/png";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
