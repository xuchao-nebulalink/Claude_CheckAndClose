#!/bin/bash
set -euo pipefail
SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_NAME='Claude 一键工具'
APP_SOURCE_DIR="$SOURCE_DIR"
if [ -d "$SOURCE_DIR/build/$APP_NAME.app" ]; then
    APP_SOURCE_DIR="$SOURCE_DIR/build"
fi
INSTALL_DIR="$HOME/Applications/Claude一键工具"
source_app="$APP_SOURCE_DIR/$APP_NAME.app"
installed_app="$INSTALL_DIR/$APP_NAME.app"
desktop_item="$HOME/Desktop/$APP_NAME.app"

# Refuse to overwrite an unrelated app or an existing desktop item with the same name.
if [ ! -d "$source_app" ]; then
    printf '请先运行 bash build.sh：%s\n' "$source_app" >&2
    exit 1
fi
# Validate the complete source bundle before touching a working installation.
/usr/bin/codesign --verify --deep --strict "$source_app"
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

mkdir -p "$INSTALL_DIR"
/usr/bin/ditto "$source_app" "$installed_app"
/usr/bin/codesign --verify --deep --strict "$installed_app"
if [ ! -L "$desktop_item" ]; then
    ln -s "$installed_app" "$desktop_item"
fi

# Earlier versions installed separate check and close apps. Remove only the items
# they created, identified by the installed bundle ID and the exact desktop link.
remove_previous_entry() {
    local app_name="$1" identifier="$2"
    local previous_app="$INSTALL_DIR/$app_name.app" previous_link="$HOME/Desktop/$app_name.app"
    if [ -L "$previous_link" ] && [ "$(readlink "$previous_link")" = "$previous_app" ]; then
        rm "$previous_link"
    fi
    if [ -d "$previous_app" ] && [ ! -L "$previous_app" ] &&
        [ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$previous_app/Contents/Info.plist" 2>/dev/null)" = "$identifier" ]; then
        rm -rf "$previous_app"
    fi
}
remove_previous_entry '一键关闭 Claude' 'local.xuchao.claudecontrol.close'
remove_previous_entry '检查 Claude 进程' 'local.xuchao.claudecontrol.status'
printf '已安装到：%s\n桌面已创建入口。\n' "$installed_app"
