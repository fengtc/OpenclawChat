namespace OpenclawChat.Models;

/// <summary>
/// 一个租户对应一台独立的 OpenClaw 网关 + 一组用户。租户 Id 同时作为 URL 前缀。
/// </summary>
public sealed class Tenant
{
    /// <summary>租户内部 Id（自动生成的 12 位短 uuid，用于 FK）。</summary>
    public required string Id { get; set; }

    /// <summary>租户名称，必填。</summary>
    public required string Name { get; set; }

    public required string GatewayEndpoint { get; set; }

    public required string GatewayToken { get; set; }

    public string? GatewayOrigin { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
