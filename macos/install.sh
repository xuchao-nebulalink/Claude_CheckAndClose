#!/bin/bash
set -euo pipefail
SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_SOURCE_DIR="$SOURCE_DIR"
if [ -d "$SOURCE_DIR/build/一键关闭 Claude.app" ]; then
    APP_SOURCE_DIR="$SOURCE_DIR/build"
fi
INSTALL_DIR="$HOME/Applications/Claude一键工具"

# Refuse to overwrite unrelated apps or an existing desktop item with the same name.
for app_name in '一键关闭 Claude' '检查 Claude 进程'; do
    source_app="$APP_SOURCE_DIR/$app_name.app"
    installed_app="$INSTALL_DIR/$app_name.app"
    desktop_item="$HOME/Desktop/$app_name.app"
    if [ ! -d "$source_app" ]; then
        printf '请先运行 bash build.sh：%s\n' "$source_app" >&2
        exit 1
    fi
    if [ -e "$installed_app" ]; then
        source_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$source_app/Contents/Info.plist")
        installed_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$installed_app/Contents/Info.plist")
        if [ "$source_id" != "$installed_id" ]; then
            printf '目标位置已有其他应用，未覆盖：%s\n' "$installed_app" >&2
            exit 1
        fi
    fi
    if [ -e "$desktop_item" ] || [ -L "$desktop_item" ]; then
        if [ ! -L "$desktop_item" ] || [ "$(readlink "$desktop_item")" != "$installed_app" ]; then
            printf '桌面已有同名项目，未覆盖：%s\n' "$desktop_item" >&2
            exit 1
        fi
    fi
done

mkdir -p "$INSTALL_DIR"
for app_name in '一键关闭 Claude' '检查 Claude 进程'; do
    /usr/bin/ditto "$APP_SOURCE_DIR/$app_name.app" "$INSTALL_DIR/$app_name.app"
    /usr/bin/codesign --verify --deep --strict "$INSTALL_DIR/$app_name.app"
    if [ ! -L "$HOME/Desktop/$app_name.app" ]; then
        ln -s "$INSTALL_DIR/$app_name.app" "$HOME/Desktop/$app_name.app"
    fi
done
printf '已安装到：%s\n桌面已创建两个入口。\n' "$INSTALL_DIR"
