#!/bin/bash
set -euo pipefail
SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
ARCHITECTURES=" $(/usr/bin/xcrun lipo -archs "$SOURCE_DIR/build/ClaudeControl") "
for architecture in arm64 x86_64; do
    case "$ARCHITECTURES" in
        *" $architecture "*) ;;
        *) printf '缺少架构：%s\n' "$architecture" >&2; exit 1 ;;
    esac
done
printf 'Universal 架构验证通过：%s\n' "$ARCHITECTURES"
/usr/bin/codesign --verify --deep --strict "$SOURCE_DIR/build/Claude 一键工具.app"
"$SOURCE_DIR/build/ClaudeControl" --status
