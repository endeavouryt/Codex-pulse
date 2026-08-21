# Codex Pulse

Codex Pulse 是一个 Windows 11 优先的极简悬浮状态面板 MVP。默认窗口约为 `128×64` DIP，支持置顶、无边框、拖动和位置持久化。

## 运行

需要安装 .NET 6 Windows Desktop Runtime。开发机有 .NET SDK 时：

```powershell
dotnet run --project .\CodexPulse.csproj
```

发布单文件（当前机器为 x64 时）：

```powershell
dotnet publish .\CodexPulse.csproj -c Release -r win-x64 --self-contained false
```

如果 `codex.exe` 不在 `PATH`，可设置：

```powershell
$env:CODEX_PULSE_CODEX_PATH = 'C:\path\to\codex.exe'
```

## 数据源策略

1. 优先启动 `codex app-server --stdio`，使用 JSONL 请求 `initialize`、`account/rateLimits/read`、`account/usage/read` 和 `thread/list`。
2. app-server 没有返回某项数据时，读取 `%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl` 的尾部事件，解析 `event_msg/token_count`、`task_started` 和 `task_complete`。
3. CTX 候选按 thread/session 标识合并；默认自动跟随最近开始工作的会话。没有工作时保持当前会话，不会把多个会话合并到 CTX。QTA 仍按账号级 rate-limit 数据读取。
4. 未获取到真实数据时面板显示 `—`，数据源缺失会在悬浮提示中标记，不会填充模拟百分比。

CTX 使用当前/最近 token usage 与 model context window 计算剩余比例；QTA 使用 Codex rate-limit window 的 `usedPercent` 计算剩余比例。所有字段都按可选字段处理，以兼容不同 Codex CLI 版本。

窗口位置保存在 `%LOCALAPPDATA%\CodexPulse\window.json`。首次运行默认使用当前主屏幕工作区右下角（右 30px、下 24px），拖动结束后立即保存；显示器布局变化后会自动限制到可见区域。右键面板可以手动刷新或退出。

默认通过 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexPulse` 随 Windows 用户登录启动。Pulse 不修改 ChatGPT，会每 2 秒检测 `ChatGPT`/`ChatGPTDesktop` 进程：ChatGPT 运行时显示，退出后隐藏但保留后台进程。以后可在 `%LOCALAPPDATA%\CodexPulse\settings.json` 写入以下配置关闭自启动：

```json
{
  "startWithWindows": false
}
```

浅色面板使用 Windows 11 DWM transient-window backdrop 作为实际背景模糊，再叠加低透明度白色玻璃层；旧系统会退回到轻量 WPF 表面。

## 当前 MVP 边界

Windows 端 Codex app-server 目前以子进程 stdio 接入。若 Codex 桌面客户端运行在独立进程且其运行态没有通过 app-server 暴露给新连接，MVP 会用本地 session JSONL 补齐当前 task 与 CTX；缺少字段仍然会明确显示为 `—`。后续可以在不改 UI 的情况下替换 `AppServerProvider` 或增加更直接的 app-server 控制 socket 连接。
