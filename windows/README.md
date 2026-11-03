# Windows 版

支持 .NET 10 支持的 Windows 版本，提供 `win-x64`（常见 Intel/AMD 电脑）和 `win-arm64`（ARM Windows）自包含包。使用者无需安装 .NET；开发构建需要 .NET 10 SDK。

解压后双击 **ClaudeControl.exe**：窗口打开后自动检查，列出进程和 Cowork 服务状态。需要关闭时点 **一键关闭 Claude**，关闭后自动复查；可随时点 **重新检查**。确认没有需要关闭的内容时，不显示关闭按钮。

可右键 `ClaudeControl.exe` 创建桌面快捷方式。如果希望打开后直接关闭，可在快捷方式目标末尾加上 `--close`；这样不会先让你确认。关闭会中断 Claude 对话、Claude Code、Cowork 和相关本地任务，请先保存工作。

从旧版升级时请解压到新文件夹，并删除旧文件夹和指向旧 `.cmd` 的快捷方式：旧版的 `Close Claude.cmd` 仍会不经确认直接关闭。

## 检查和关闭范围

普通进程管理仅处理**当前 Windows 用户、当前登录会话**的本地进程。精确识别可执行文件名 `Claude.exe` / `claude.exe`、Claude 专属安装目录中的桌面辅助进程，以及 Node/Bun 实际运行的 `node_modules/@anthropic-ai/claude-code/cli.js` 入口；追踪快照中可确定归属的后代。不会根据整条命令行里的“Claude”文字模糊结束进程。只接受已明确支持的运行时选项；不认识的选项后出现精确 Claude 脚本线索时，报告检查不完整并保留该进程。`bun run` 仅将绝对或 `./` / `.\` 开头的明确文件入口纳入，其他可能与 package.json 脚本重名的形式会警告。

先向已识别进程的主窗口发送正常关闭请求，再以经过身份复核的本机进程句柄终止残留。CLI 没有通用的安全退出协议，因此会直接结束；不会向共享终端发送 Ctrl+C。每次终止都复核 PID、进程创建时间、用户 SID 和登录会话；不会根据 PID 单独操作。WMI 的父进程和脚本入口也需与本机创建时间关联后才采用。父级终端、编辑器和本工具祖先进程受保护。如果从 Claude 的子终端打开工具，受保护的 Claude 祖先进程仍会显示，并提示从桌面独立打开；活动祖先身份无法核实时，本次禁止执行关闭。

读取权限不足、脚本入口不可读、WMI 检查失败或结束失败都会显示未完成信息；不会以“零个已识别进程”掩盖读取失败。已捕获的子任务暂时无法读取时保留身份并在后续复查，不能把读失败当作退出。已脱离父级且无法追溯的孤立任务、其他会话/用户、WSL、Cowork 虚拟机内部任务、其他系统服务、远程任务、浏览器中的 Claude 网页不在范围内。启动器持续重启 Claude 时，最终复查会显示能检测到的残留。

## Cowork 后台服务

检查时会另外显示 Cowork 服务状态，即使当前用户进程为零也不会省略服务结果。只读检查不会弹出管理员授权。

一键关闭先处理原用户的本地 Claude 进程，再核实 Cowork 服务并通过 Windows 服务控制管理器请求停止。Cowork 服务是整台电脑共享的机器级服务，停止可能中断其他登录用户的 Cowork 任务。服务名、可执行文件路径和发布者身份无法核实时拒绝停止并显示警告；不会仅凭任务管理器中的 `cowork-svc.exe` 名字结束任意进程。

需要权限时弹出 UAC 管理员授权，提升的辅助程序仅负责重新验证并停止 Cowork 服务，不重新扫描或关闭管理员账号下的用户进程。取消授权、拒绝访问、服务不接受停止或超时都会显示未完成；不会改用强制结束服务 PID 的方式绕过失败。

工具不启动服务，不更改启动类型，不禁用或删除服务，也不停止 `vmcompute`、`hns` 等共享系统服务。下一次 Claude 启动、更新或系统重启仍可能重新启动它。服务停止只是已检查到的服务状态，不证明虚拟机内部、远程任务或所有网络活动都已停止。

## 网络与隐私

应用本身仅使用本机进程、令牌、服务管理和本地 WMI API；服务身份校验仅使用本地信息，不进行在线证书查询。没有 HTTP 请求、遥测、自动更新，也不改代理、DNS、防火墙或 Claude 配置。命令行参数仅在内存中用于识别脚本，结果不输出参数或用户 SID。

**工具没有联网功能，不等于网络隔离或防 IP 泄漏。** Claude 在运行、退出等待期间已有的连接仍可能通信；工具无法撤回之前已发送的数据，也不能阻止之后重新启动。若准备切换/关闭代理，先在代理仍启用时关闭 Claude，核对用户进程和 Cowork 服务结果后再操作。Cowork 在 Windows 使用后台服务和虚拟机（参见 [Anthropic 官方部署说明](https://support.claude.com/en/articles/12622703-deploy-claude-desktop-for-windows)）；本工具不直接检查虚拟机内部，也不把服务停止描述为所有通信已停止。要严格阻断网络，需要独立的系统网络控制。

## 构建与测试

在 Windows PowerShell 或 PowerShell 7 中运行（脚本不更改系统执行策略）：

```powershell
./windows/test.ps1
./windows/build.ps1 -Runtime win-x64
# ARM Windows 包：
./windows/build.ps1 -Runtime win-arm64
```

输出位于 `dist/ClaudeControl-windows-win-x64.zip` 等文件。首次构建由 .NET/NuGet 下载 Microsoft 框架与包；编译后的应用运行时不执行这些下载。脚本为其自身 .NET SDK 进程关闭 SDK 遥测。

测试包含跨平台合成快照的匹配、误匹配、后代、祖先保护、PID 复用、失败报告；Windows 上另启动临时无害模拟程序验证 Win32/WMI、参数解析和定向终止。集成测试只结束自身创建的模拟子进程，不调用对真实系统的完整关闭流程。

可在 macOS/Linux 运行纯逻辑部分：

```sh
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --project windows/ClaudeControl.Tests -c Release
```

发布后的只读检查可把 JSON 写到指定本地文件，适合 WinExe 的自动化测试：

```powershell
$exe = './dist/ClaudeControl-windows-win-x64/ClaudeControl.exe'
$result = Join-Path $env:TEMP 'claude-status.json'
$process = Start-Process $exe -ArgumentList @('--status-json', ('"' + $result + '"')) -Wait -PassThru
Get-Content $result
```

`remaining_count` 只统计用户进程，必须同时查看 `cowork_service`。`confirmed_closed` 仅在覆盖范围内的用户进程为零、服务停止或未安装且没有警告时为 true；不代表网络隔离。`scan_complete` 为 false 时不能视为确认关闭；退出码为 2。检查完成的退出码 0 不表示一定没有运行中的目标。此命令只检查，不结束进程或停止服务，也不弹出管理员授权。

原生 API 行为参考：[CloseMainWindow](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.closemainwindow?view=net-10.0)、[GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)、[TerminateProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess)、[CommandLineToArgvW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw)。
