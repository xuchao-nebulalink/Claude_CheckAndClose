# Claude Check & Close

本地检查与一键关闭 Claude 的小工具，支持 macOS 和 Windows。

只有一个应用：打开后自动检查并列出进程，需要关闭时点 **一键关闭**。关闭后会再次检查并显示残留、受保护进程或读取失败。关闭会中断 Claude 正在进行的对话、Claude Code 和本地子任务，使用前请保存工作。

## 下载与使用

从 [Releases](https://github.com/xuchao-nebulalink/Claude_CheckAndClose/releases/latest) 下载对应系统的 ZIP 并完整解压。GitHub 的 **Code → Download ZIP** 下载的是源码，不能代替构建好的应用。

| 系统 | 下载包 | 使用方式 |
| --- | --- | --- |
| macOS 12 及以上，Apple Silicon / Intel | `ClaudeControl-macOS-universal.zip` | 双击 `Claude 一键工具.app`；可运行 `安装桌面按钮.command` 创建桌面入口，也可拖到 Dock |
| Windows x64 | `ClaudeControl-windows-win-x64.zip` | 双击 `ClaudeControl.exe` |
| Windows ARM64 | `ClaudeControl-windows-win-arm64.zip` | 双击 `ClaudeControl.exe` |

从旧版升级时，macOS 运行 `安装桌面按钮.command` 会替换旧版安装的两个应用和桌面入口，留在 Dock 的旧图标需手动移除。Windows 请解压到新文件夹，并删除旧文件夹和指向旧 `.cmd` 的快捷方式：旧版的 `Close Claude.cmd` 仍会不经确认直接关闭。

Windows 发布包包含 .NET 运行时，无需另装 .NET。Windows 版会另外检查 Cowork 后台服务；关闭需要额外权限时会弹出 Windows 管理员授权提示，仅对经过身份核验的服务执行停止操作。

macOS 应用采用本地临时签名，Windows 应用未购买代码签名证书。下载后系统可能提示无法验证开发者；没有配置 Apple 公证或商业签名。可以在自己的电脑从源码构建，不需要关闭系统安全保护。

## 会不会把我的 IP 发给 Claude？

**本工具不请求 Claude / Anthropic 服务，不读取 Claude 账号配置或聊天记录文件，不提取、记录或上传 API Key，也没有遥测、联网检查或自动更新。** 检查和关闭使用本机系统 API，脚本入口参数仅在内存中用于进程识别。运行应用不需要联网；从 GitHub 下载、源码构建时下载依赖属于单独的操作。

但这不等于保证真实 IP 不会暴露：Claude 原本运行时，以及正常退出前的短暂等待期间，仍可能自行联网；本工具不能撤回它已建立的连接或服务端已记录的 IP，也不会阻止它之后被重新打开。

如果你准备切换或关闭代理，对本工具覆盖的本机进程应当：**先保存工作 → 关闭 Claude → 确认没有残留且检查完整 → 再切换网络**。Windows 版会分别报告用户进程与 Cowork 后台服务状态；服务停止不等于已经核实虚拟机内部任务或全部网络活动停止。其他系统服务、网页及远程任务仍需另外确认。需要严格防止进程绕过代理直连时，还需要独立的网络隔离措施。完整说明见 [隐私与能力边界](docs/PRIVACY.md)。

## 识别与结束范围

- macOS：当前用户的 Claude 桌面版、内置与独立 Claude Code、明确的 Node/Bun CLI 脚本入口及能追溯的子进程。
- Windows：当前用户、当前登录会话内的对应本地进程；另外单独检查并请求停止身份核验通过的 Cowork 后台服务。详细规则见 [Windows 说明](windows/README.md)。
- 按真实可执行文件和实际脚本入口识别，不扫描整条命令中的任意 `claude` 文本。普通浏览器参数、笔记文件名不会成为匹配依据。
- 结束前复核进程身份，防止 PID 被另一个进程复用。保留工具自身及其祖先；受保护的 Claude 祖先仍应在结果中显示，建议从桌面独立启动工具再处理。
- 权限或读取失败会提示检查不完整，不能把这类结果当作“已经全部关闭”。

浏览器中的 Claude 网页、远程主机、Cowork/其他虚拟机内部、其他系统服务、容器/WSL 内的任务、已完全脱离且无法追溯来源的孤立进程不在保证识别范围内。工具不停止 Windows 的共享虚拟化服务，不更改服务启动类型、不禁用或删除服务。结果是复查时的快照，不能保证扫描间隙中的短命进程或稍后重启的进程始终不存在。

## 从源码构建

macOS 需要系统的 Xcode Command Line Tools：

```bash
bash macos/test.sh
bash macos/build.sh
bash macos/verify.sh
bash macos/install.sh
```

只读 JSON 检查，不执行关闭：

```bash
./macos/build/ClaudeControl --status
```

不带参数运行时打开检查窗口；只有明确传入 `--close` 才会不经确认直接关闭；无法识别的参数会报错退出，不执行任何动作。

Windows 需要 .NET 10 SDK 和 PowerShell，构建与打包命令见 [Windows 说明](windows/README.md)。发布包本身无需 SDK。

## 验证与发布

[GitHub Actions](https://github.com/xuchao-nebulalink/Claude_CheckAndClose/actions) 在 Apple Silicon、Intel Mac、Windows x64、Windows ARM64 上运行测试和构建。Windows 平台集成测试只结束测试自行创建的模拟进程。macOS 测试同样不调用面向所有 Claude 的实际关闭操作。

每次推送都生成工作流构件；`v*` 标签在全部测试与构建成功后发布 ZIP 和 `SHA256SUMS.txt`。查看 [验证说明](docs/TESTING.md) 了解测试覆盖与剩余限制。测试通过不代表已验证每一种 Claude 安装方式或网络环境。

版本号由根目录 `VERSION` 统一提供。另见 [进程边界复查记录](docs/REVIEW.md)，包括 PID 复用、未知 Node/Bun 入口与安装目录依赖的检查范围。
