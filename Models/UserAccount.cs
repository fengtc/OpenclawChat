namespace OpenclawChat.Models;

public sealed class UserAccount
{
    /// <summary>所属租户 Id（URL 前缀）。</summary>
    public required string TenantId { get; set; }

    public required string Username { get; set; }

    /// <summary>全局唯一邮箱，用作登录账号。</summary>
    public string? Email { get; set; }

    /// <summary>未激活时为空。Base64(salt) + ":" + Base64(PBKDF2 hash)。</summary>
    public string? PasswordHash { get; set; }

    /// <summary>对应 OpenClaw agent 名（与 sessionKey 等同）。</summary>
    public required string AgentName { get; set; }

    public bool IsAdmin { get; set; }

    /// <summary>是否已激活（设置过密码）。未激活的账号不能登录。</summary>
    public bool IsActivated { get; set; }

    /// <summary>邀请未激活时使用的一次性 token。</summary>
    public string? ActivationToken { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ActivatedAt { get; set; }
}
