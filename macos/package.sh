#!/bin/bash
set -euo pipefail
SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SOURCE_DIR/.." && pwd)"
STAGING_DIR="$SOURCE_DIR/build/package/ClaudeControl-macOS"
DIST_DIR="$ROOT_DIR/dist"
if [ -d "$STAGING_DIR" ]; then rm -rf "$STAGING_DIR"; fi
mkdir -p "$STAGING_DIR" "$DIST_DIR"
APP_NAME='Claude 一键工具'
/usr/bin/codesign --verify --deep --strict "$SOURCE_DIR/build/$APP_NAME.app"
/usr/bin/ditto "$SOURCE_DIR/build/$APP_NAME.app" "$STAGING_DIR/$APP_NAME.app"
cp "$SOURCE_DIR/install.sh" "$STAGING_DIR/安装桌面按钮.command"
chmod +x "$STAGING_DIR/安装桌面按钮.command"
cp "$ROOT_DIR/docs/PRIVACY.md" "$STAGING_DIR/PRIVACY.md"
cat > "$STAGING_DIR/使用说明.txt" <<'INFO'
Claude Check & Close — macOS 12+（Apple Silicon / Intel）

双击“Claude 一键工具.app”：自动检查并列出进程；需要关闭时点“一键关闭”，关闭后复查并显示结果。
双击“安装桌面按钮.command”：复制应用并创建桌面入口；旧版安装的两个应用和桌面入口会被替换。
也可以把 .app 拖到 Dock；旧版留在 Dock 的图标需手动移除。

关闭会中断正在执行的 Claude 任务，请先保存工作。
如有残留、受保护进程或检查不完整提示，应先处理，不能认为已全部关闭。

本工具运行时没有联网代码，但 Claude 在退出前仍可能自行联网。
准备切换代理时，请先关闭、确认检查完整且没有残留，再切换网络。
详见同目录 PRIVACY.md。

下载版采用临时签名，未进行 Apple 公证；可选择在本机从源码构建。
源码、构建与测试说明：
https://github.com/xuchao-nebulalink/Claude_CheckAndClose
INFO
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$STAGING_DIR" "$DIST_DIR/ClaudeControl-macOS-universal.zip"
/usr/bin/unzip -t "$DIST_DIR/ClaudeControl-macOS-universal.zip" >/dev/null
printf '已生成：%s\n' "$DIST_DIR/ClaudeControl-macOS-universal.zip"
