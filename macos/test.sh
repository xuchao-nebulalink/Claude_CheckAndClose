#!/bin/bash
set -euo pipefail
SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$SOURCE_DIR/build"
/usr/bin/xcrun clang -fobjc-arc -O2 -Wall -Wextra -Wno-unused-parameter -Wno-unused-function \
    -mmacosx-version-min=12.0 -framework AppKit -framework Foundation \
    "$SOURCE_DIR/Tests.m" -o "$SOURCE_DIR/build/ClaudeControlTests"
"$SOURCE_DIR/build/ClaudeControlTests"
