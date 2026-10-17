#!/bin/sh
# Repro for the Uno 6.8 macOS stale-pixel bug: runs the spike's "stale" variant, then captures the window
# repeatedly after the red block was removed and counts captures that still show red where it used to be.
# Usage: stale-repro.sh <spike output dir> [metal|software]
set -eu
out=$1
mode=${2:-metal}
tmp=$(mktemp -d)
trap 'pkill -x UnoHostMac 2>/dev/null || true; rm -rf "$tmp"' EXIT

cat > "$tmp/wl.swift" <<'EOF'
import CoreGraphics
let l = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as! [[String: Any]]
for w in l where (w["kCGWindowOwnerName"] as? String) == "UnoHostMac" { print(w["kCGWindowNumber"]!); break }
EOF

software=0
[ "$mode" = software ] && software=1
(cd "$out" && SPIKE_TEST=stale UNO_SPIKE_SOFTWARE=$software DEVFLOW_AGENT=0 ./UnoHostMac >"$tmp/log" 2>&1 &)
sleep 5
id=$(swift "$tmp/wl.swift")
grep -q "red block removed" "$tmp/log" || { echo "block not removed yet"; exit 1; }

stale=0
for i in $(seq 1 20); do
	screencapture -x -o -l "$id" "$tmp/c$i.png"
	if python3 -I -c '
import sys
from PIL import Image
im = Image.open(sys.argv[1]).convert("RGB")
# Centre of the removed block: content (120, 90) + 28 pt title bar, at 2x backing scale.
r, g, b = im.getpixel((240, 236))
sys.exit(0 if r > 180 and g < 80 and b < 80 else 1)
' "$tmp/c$i.png"; then stale=$((stale + 1)); fi
	sleep 0.1
done
echo "$mode: $stale/20 captures still show the removed red block"
