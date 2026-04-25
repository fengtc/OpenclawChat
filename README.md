# OpenClaw WebChat (Blazor Server)

基于 Blazor Server 实现，按 OpenClaw 最新 WebUI 交互逻辑对齐 Gateway 协议。

## 握手兼容策略
- 建连后会短等待 `connect.challenge`（约 750ms）；若未收到会直接继续发送 `connect`。
- 仅通过 WebSocket `Origin` 请求头处理跨域校验，不在 `connect.params` 里发送 `origin`（兼容严格 schema 的网关版本）。
## 协议对齐
- 握手：`connect.challenge` -> `connect`
- RPC：`chat.history` / `chat.send` / `chat.abort`
- 事件：`chat`（`delta/final/aborted/error`）+ `agent`（`tool/compaction/fallback`）

## 已复刻交互细节
- 流式渲染与 reading indicator
- `NO_REPLY` 过滤策略
- 队列发送（busy 时 Queue）
- Stop/New session 行为
- Tool stream 卡片与侧边栏详情
- Compaction/Fallback 状态提示
- 自动滚动与 New messages 提示
- 图片附件上传与预览发送

## 启动
```powershell
dotnet run
```

页面参数：
- Gateway WS：`ws://localhost:3000/ws`
- Session Key：`main`
- Token/Password：按你的网关配置填写（可选）

## 常见连接错误
- `invalid connect params: at /auth/password: must be string`
  - 仅填 Token、不填 Password 即可；客户端现在会在 Password 为空时不发送该字段。
- `origin not allowed`
  - 先在页面的 `Origin (可选)` 填与你网关允许列表一致的值（例如 `http://localhost:3000`）。
  - 若仍报错，请在 OpenClaw 网关配置中把该 Origin 加入 `gateway.controlUi.allowedOrigins`。
