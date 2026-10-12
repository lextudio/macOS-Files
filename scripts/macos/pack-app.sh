#!/usr/bin/env bash
# Packages the Uno (Skia desktop, macOS) build into a runnable "Files.app" bundle.
#
# Usage: pack-app.sh [-o OUTDIR] [-r RID] [--no-publish] [--dmg] [--version X.Y.Z]
#   -o OUTDIR      Where Files.app (and an optional .dmg) is written. Default: <repo>/dist.
#   -r RID         Runtime identifier. Default: osx-arm64.
#   --no-publish   Reuse the last publish output (OUTDIR/.publish) instead of rebuilding.
#   --dmg          Also build a compressed disk image.
#   --version      CFBundleShortVersionString. Default: 0.1.0.
# Env: BUNDLE_ID (default io.github.lextudio.MacOSFiles).
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="$repo/dist"
rid="osx-arm64"
version="0.1.0"
bundle_id="${BUNDLE_ID:-io.github.lextudio.MacOSFiles}"
app_name="Files"
do_publish=1
do_dmg=0

while [[ $# -gt 0 ]]; do
	case "$1" in
		-o) out="$2"; shift 2 ;;
		-r) rid="$2"; shift 2 ;;
		--version) version="$2"; shift 2 ;;
		--no-publish) do_publish=0; shift ;;
		--dmg) do_dmg=1; shift ;;
		*) echo "unknown option: $1" >&2; exit 2 ;;
	esac
done

publish_dir="$out/.publish"
app="$out/$app_name.app"

if [[ "$do_publish" == 1 ]]; then
	echo "[pack] publishing ($rid, self-contained)"
	rm -rf "$publish_dir"
	MSBUILDDISABLENODEREUSE=1 dotnet publish "$repo/src/Files.App/Files.App.csproj" \
		-c Release -f net10.0-desktop -r "$rid" --self-contained true \
		-p:PublishTrimmed=false \
		-o "$publish_dir" -nodeReuse:false -m:2
fi
[[ -f "$publish_dir/Files" ]] || { echo "[pack] no publish output at $publish_dir (run without --no-publish first)" >&2; exit 1; }

echo "[pack] assembling $app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# The app reads its assets from AppContext.BaseDirectory, so everything stays next to Files.dll.
rsync -a --exclude '*.pdb' "$publish_dir/" "$app/Contents/MacOS/"

# Inside a recognized bundle Uno resolves ms-appx:/// to <MacOS>/Resources/, so mirror every sibling
# entry there (relative symlinks keep the bundle self-contained and portable).
mkdir -p "$app/Contents/MacOS/Resources"
for entry in "$app/Contents/MacOS"/*; do
	name="$(basename "$entry")"
	[[ "$name" == "Resources" ]] && continue
	ln -sfn "../$name" "$app/Contents/MacOS/Resources/$name"
done

# --- icon: build AppIcon.icns from the largest square app-tile PNG ---
source_png=""
best=0
while IFS= read -r png; do
	w="$(sips -g pixelWidth "$png" 2>/dev/null | awk '/pixelWidth/{print $2}')"
	h="$(sips -g pixelHeight "$png" 2>/dev/null | awk '/pixelHeight/{print $2}')"
	# Skip the wide splash/banner art; icons must be square.
	if [[ -n "${w:-}" && "$w" = "${h:-}" && "$w" -gt "$best" ]]; then best="$w"; source_png="$png"; fi
done < <(find "$repo/src/Files.App/Assets/AppTiles" -name '*.png' -not -path '*contrast*')
[[ -n "$source_png" ]] || source_png="$repo/src/Files.App/Assets/FolderIcon.png"

if [[ -n "$source_png" && -f "$source_png" ]]; then
	echo "[pack] icon source: $source_png (${best}px)"
	composed="$out/AppIcon.png"
	icon_src="$source_png"
	if MSBUILDDISABLENODEREUSE=1 dotnet run --project "$repo/tools/MacOSIconGen/MacOSIconGen.csproj" -c Release -- "$source_png" "$composed" 1024 >/dev/null 2>&1 && [[ -f "$composed" ]]; then
		icon_src="$composed"
	else
		echo "[pack] icon generator unavailable; using the raw logo"
	fi
	iconset="$out/AppIcon.iconset"
	rm -rf "$iconset"; mkdir -p "$iconset"
	for size in 16 32 128 256 512; do
		sips -z "$size" "$size" "$icon_src" --out "$iconset/icon_${size}x${size}.png" >/dev/null
		sips -z "$((size * 2))" "$((size * 2))" "$icon_src" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
	done
	iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
	rm -rf "$iconset" "$composed"
fi

# --- Info.plist ---
cat >"$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleName</key><string>$app_name</string>
	<key>CFBundleDisplayName</key><string>$app_name</string>
	<key>CFBundleIdentifier</key><string>$bundle_id</string>
	<key>CFBundleExecutable</key><string>$app_name</string>
	<key>CFBundleIconFile</key><string>AppIcon</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
	<key>CFBundleShortVersionString</key><string>$version</string>
	<key>CFBundleVersion</key><string>$version</string>
	<key>LSMinimumSystemVersion</key><string>11.0</string>
	<key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
	<key>NSHighResolutionCapable</key><true/>
	<key>NSHumanReadableCopyright</key><string>Files Community</string>
	<key>CFBundleDocumentTypes</key>
	<array>
		<dict>
			<key>CFBundleTypeName</key><string>Folder</string>
			<key>CFBundleTypeRole</key><string>Viewer</string>
			<key>LSHandlerRank</key><string>Alternate</string>
			<key>LSItemContentTypes</key>
			<array><string>public.folder</string></array>
		</dict>
		<dict>
			<key>CFBundleTypeName</key><string>Item</string>
			<key>CFBundleTypeRole</key><string>Viewer</string>
			<key>LSHandlerRank</key><string>Alternate</string>
			<key>LSItemContentTypes</key>
			<array><string>public.item</string></array>
		</dict>
	</array>
</dict>
</plist>
PLIST

# --- ad-hoc sign so TCC/LaunchServices treat it as a proper bundle ---
# --deep mis-parses the Uno.Fonts.Fluent data directory as a nested bundle, so nested dylibs are
# signed individually and the bundle is signed without --deep.
echo "[pack] ad-hoc signing"
find "$app/Contents/MacOS" -name '*.dylib' -exec codesign --force -s - {} \; >/dev/null 2>&1 || true
codesign --force -s - "$app" >/dev/null 2>&1 || true
if codesign -dv "$app" >/dev/null 2>&1; then
	echo "[pack] signed (ad-hoc)"
else
	echo "[pack] warning: bundle is unsigned"
fi

echo "[pack] done: $app"

if [[ "$do_dmg" == 1 ]]; then
	dmg="$out/$app_name-$version-$rid.dmg"
	echo "[pack] building $dmg"
	rm -f "$dmg"
	hdiutil create -volname "$app_name" -srcfolder "$app" -ov -format UDZO "$dmg" >/dev/null
	echo "[pack] done: $dmg"
fi
