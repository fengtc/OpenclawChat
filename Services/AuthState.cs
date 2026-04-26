using OpenclawChat.Models;

namespace OpenclawChat.Services;

/// <summary>
/// 当前登录态（按 Blazor circuit 作用域）。简单实现，不接 ASP.NET Core Identity。
/// </summary>
public sealed class AuthState
{
    public UserAccount? CurrentUser { get; private set; }

    public bool IsAuthenticated => CurrentUser is not null;

    public bool IsAdmin => CurrentUser?.IsAdmin == true;

    public string? TenantId => CurrentUser?.TenantId;

    public event Action? StateChanged;

    public bool IsAuthenticatedFor(string? tenantId)
    {
        return CurrentUser is not null
            && !string.IsNullOrWhiteSpace(tenantId)
            && string.Equals(CurrentUser.TenantId, tenantId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public void SignIn(UserAccount user)
    {
        CurrentUser = user ?? throw new ArgumentNullException(nameof(user));
        StateChanged?.Invoke();
    }

    public void SignOut()
    {
        CurrentUser = null;
        StateChanged?.Invoke();
    }
}
