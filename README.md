# OpenClaw Chat

OpenClaw Chat 是一个基于 Blazor Server 的 OpenClaw 网页聊天客户端，直接连接 OpenClaw Gateway WebSocket，提供登录、多用户、会话历史、后端流式回复、工具流展示和 Markdown 渲染。

## 主要能力

- 登录后自动连接网关，无需手动点击连接。
- 默认进入当前用户 Agent 的主会话，例如 `agent:main:main`、`agent:qq:main`。
- 助手正文默认使用 OpenClaw 后端流式事件；`chat.history` 只作为延迟兜底。
- 支持会话历史读取，调用后端 `chat.history`，当前页面默认拉取最近 200 条。
- 支持按日期、关键词筛选会话标识。
- 管理员可查看全部会话；普通用户只能查看自己 Agent 下的会话。
- “新会话”不会发送 `/new`，而是生成新的日期会话，例如 `agent:qq:20260428-164530-a1b2c3`，避免清空主会话历史。
- 支持 Markdown 表格、代码块、列表等渲染。
- 支持显示/隐藏思考、显示/隐藏工具流。
- 支持图片附件、复制回复到输入框、停止当前回复。
- 支持租户初始化、管理员登录、邀请普通用户、激活账号。

## 技术栈

- .NET / Blazor Server
- OpenClaw Gateway WebSocket
- SQLite 用户与租户存储
- Markdig Markdown 渲染
- ASP.NET Core Data Protection 本地密钥持久化

## 启动

```powershell
dotnet run
```

默认使用 HTTPS，本地页面通常为：

```text
https://localhost:7179
```

首次使用请打开：

```text
/setup
```

创建租户、管理员账号并填写 OpenClaw Gateway 信息。

## 配置

`appsettings.json` 中保留了默认网关配置结构：

```json
{
  "OpenclawConnection": {
    "Endpoint": "ws://claw.blsc.dev/ws",
    "Token": "",
    "Password": null,
    "Origin": "https://claw.blsc.dev/",
    "SessionKey": "main"
  }
}
```

多用户模式下，实际连接信息以 `/setup` 创建的租户数据为准，保存在 SQLite 数据库中。

数据库路径默认：

```text
<应用输出目录>/openclaw-chat.db
```

可通过配置覆盖：

```json
{
  "UserStore": {
    "DatabasePath": "E:\\source\\OpenclawChat\\openclaw-chat.db"
  }
}
```

Data Protection 密钥保存在项目目录：

```text
.data-protection-keys/
```

这是为了避免 Blazor Server 在某些 Windows 浏览器环境下因用户目录权限导致页面一直转圈。

## OpenClaw 协议

客户端对齐以下 Gateway 方法和事件：

- 握手：`connect.challenge` -> `connect`
- RPC：`sessions.list`、`chat.history`、`chat.send`、`chat.abort`
- 事件：`chat`、`agent`

助手正文默认使用后端流式事件：

- `agent` 事件中的 `stream=assistant`
- `chat` 事件中的 `state=delta/final/aborted/error`

历史兜底使用：

```text
chat.history
```

为了避免重复显示，历史兜底不会和正在进行的流式正文同时抢渲染；只有在短时间内没有收到正文流式内容时才启用。

## 会话规则

默认主会话格式：

```text
agent:{AgentName}:main
```

示例：

```text
agent:main:main
agent:qq:main
```

新会话格式：

```text
agent:{AgentName}:yyyyMMdd-HHmmss-xxxxxx
```

示例：

```text
agent:qq:20260428-164530-a1b2c3
```

这样可以按日期筛选历史，同时避免 `/new` 重置当前后端历史。

## 常见问题

### 登录后页面一直转圈

通常是 Data Protection 密钥目录权限问题。当前项目已配置为写入 `.data-protection-keys/`，请确认应用进程对项目目录有写权限。

### 已连接但没有历史

确认当前会话标识是否为对应 Agent 的主会话，例如：

```text
agent:qq:main
```

如果选择了日期会话，只会看到该日期会话的历史。

### 表格没有渲染成表格

项目使用 Markdig，并启用了 Pipe Tables / Grid Tables。Markdown 表格需要列数完整，例如：

```markdown
| 文件 | 用途 | 行数 |
|------|------|------|
| A.cs | 示例 | 10 |
```

### `origin not allowed`

请在 `/setup` 或租户配置中填写后端允许的 Origin，例如：

```text
https://localhost:7179
```

同时确认 OpenClaw Gateway 的 allowed origins 配置包含该来源。

### 邀请用户后还不能聊天

用户管理会生成后端 agent 创建命令，例如：

```text
openclaw agents add qq --workspace ~/.openclaw/workspace-qq
```

需要在 OpenClaw 后端实际创建对应 agent，前端账号和后端 agent 才能对应。

## 操作手册

完整操作说明见：

[USER_MANUAL.md](USER_MANUAL.md)
