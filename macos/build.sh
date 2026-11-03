#!/bin/bash
set -euo pipefail
SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="$SOURCE_DIR/build"
VERSION="$(tr -d '\r\n' < "$SOURCE_DIR/../VERSION")"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf 'VERSION 必须为三段数字版本号。\n' >&2
    exit 1
fi
mkdir -p "$BUILD_DIR"
/usr/bin/xcrun clang -fobjc-arc -O2 -Wall -Wextra -Wno-unused-parameter \
    -Wno-unused-function -arch arm64 -arch x86_64 \
    -mmacosx-version-min=12.0 -framework AppKit -framework Foundation \
    "$SOURCE_DIR/ClaudeControl.m" -o "$BUILD_DIR/ClaudeControl"
/usr/bin/xcrun clang -fobjc-arc -O2 -framework AppKit -framework Foundation \
    "$SOURCE_DIR/IconMaker.m" -o "$BUILD_DIR/IconMaker"

APP_NAME='Claude 一键工具'
APP_DIR="$BUILD_DIR/$APP_NAME.app"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp "$BUILD_DIR/ClaudeControl" "$APP_DIR/Contents/MacOS/ClaudeControl"
"$BUILD_DIR/IconMaker" "$BUILD_DIR/AppIcon.iconset"
/usr/bin/iconutil -c icns "$BUILD_DIR/AppIcon.iconset" -o "$APP_DIR/Contents/Resources/AppIcon.icns"
cat > "$APP_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>ClaudeControl</string>
<key>CFBundleIdentifier</key><string>local.xuchao.claudecontrol</string>
<key>CFBundleName</key><string>$APP_NAME</string>
<key>CFBundleDisplayName</key><string>$APP_NAME</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>$VERSION</string>
<key>CFBundleShortVersionString</key><string>$VERSION</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>LSUIElement</key><true/>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
/usr/bin/codesign --force --sign - "$APP_DIR"
printf '已构建：%s\n' "$APP_DIR"
