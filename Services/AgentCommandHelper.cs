namespace OpenclawChat.Services;

/// <summary>
/// 由于网关 WS 协议没有提供创建 agent 的方法，
/// agent 实际由管理员通过 SSH + docker exec 在后端手动执行 openclaw CLI 创建。
/// 这里只负责生成提示用的命令片段。
/// </summary>
public static class AgentCommandHelper
{
    /// <summary>例如 <c>openclaw agents add work --workspace ~/.openclaw/workspace-work</c>。</summary>
    public static string BuildAddCommand(string agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            return string.Empty;
        }

        var name = agentName.Trim();
        return $"openclaw agents add {name} --workspace ~/.openclaw/workspace-{name}";
    }
}
