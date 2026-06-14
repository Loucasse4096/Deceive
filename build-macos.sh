#!/usr/bin/env bash
#
# Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
#
# Builds a self-contained Deceive.app bundle for macOS (Apple Silicon by default).
#
# Usage:
#   ./build-macos.sh                # builds for osx-arm64 (Apple Silicon)
#   RID=osx-x64 ./build-macos.sh    # builds for Intel Macs
#
set -euo pipefail

RID="${RID:-osx-arm64}"
CONFIG="${CONFIG:-Release}"
PROJECT="Deceive/Deceive.csproj"
APP_NAME="Deceive"
VERSION="$(grep -oE '<Version>[^<]+' "$PROJECT" | head -1 | sed 's/<Version>//')"
OUT_DIR="artifacts"
BUNDLE="$OUT_DIR/$APP_NAME.app"
PUBLISH_DIR="$OUT_DIR/publish-$RID"

echo ">> Building $APP_NAME $VERSION for $RID ($CONFIG)"

rm -rf "$BUNDLE" "$PUBLISH_DIR"
mkdir -p "$OUT_DIR"

# 1. Publish a self-contained build (no .NET install required on the target Mac).
dotnet publish "$PROJECT" \
  -c "$CONFIG" \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -o "$PUBLISH_DIR"

# 2. Assemble the .app bundle skeleton.
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
cp -R "$PUBLISH_DIR/." "$BUNDLE/Contents/MacOS/"
chmod +x "$BUNDLE/Contents/MacOS/$APP_NAME"

# 3. App icon: build an .icns from the tray PNG if the macOS tools are available.
ICON_PNG="Deceive/Resources/deceive.png"
if command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
  ICONSET="$OUT_DIR/Deceive.iconset"
  rm -rf "$ICONSET"; mkdir -p "$ICONSET"
  for size in 16 32 64 128 256 512; do
    sips -z "$size" "$size" "$ICON_PNG" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    sips -z $((size*2)) $((size*2)) "$ICON_PNG" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$BUNDLE/Contents/Resources/Deceive.icns"
  rm -rf "$ICONSET"
  ICON_LINE='<key>CFBundleIconFile</key><string>Deceive</string>'
else
  echo ">> sips/iconutil not found (not on macOS?); shipping PNG without .icns"
  cp "$ICON_PNG" "$BUNDLE/Contents/Resources/deceive.png"
  ICON_LINE=''
fi

# 4. Info.plist.
#    NOTE: To make Deceive a pure menu-bar app with NO Dock icon, add:
#        <key>LSUIElement</key><true/>
#    It is left off by default so the startup game-picker dialog reliably comes to the front.
cat > "$BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>Deceive</string>
    <key>CFBundleIdentifier</key><string>xyz.molenzwiebel.deceive</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>$APP_NAME</string>
    $ICON_LINE
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST

# 5. Ad-hoc codesign so Gatekeeper at least recognises a (self-)signature. For personal use this
#    avoids some warnings; it is not a substitute for real notarisation.
if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$BUNDLE" || echo ">> ad-hoc codesign failed (continuing)"
fi

echo ">> Built $BUNDLE"
echo ">> If macOS refuses to open it (\"unidentified developer\"), run:"
echo "     xattr -dr com.apple.quarantine \"$BUNDLE\""
