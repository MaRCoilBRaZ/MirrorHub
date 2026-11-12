#!/usr/bin/env bash
# Adds MirrorHub to the applications menu of the current user (no root needed).
#   ./install.sh               add the menu entry (run it again after moving the folder)
#   ./install.sh --uninstall   remove the menu entry and the "start at login" entry
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
entry="$apps/mirrorhub.desktop"
autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/mirrorhub.desktop"

if [[ "${1:-}" == "--uninstall" ]]; then
  rm -f "$entry" "$autostart"
  command -v update-desktop-database >/dev/null && update-desktop-database "$apps" 2>/dev/null || true
  echo "MirrorHub removed from the applications menu. Settings stay in ~/.config/MirrorHub."
  exit 0
fi

if [[ ! -f "$here/MirrorHub" ]]; then
  echo "MirrorHub was not found next to install.sh: run it from the extracted MirrorHub folder." >&2
  exit 1
fi

# Archives extracted by some tools lose the executable bit.
chmod +x "$here/MirrorHub"
[[ -d "$here/tools/scrcpy" ]] && chmod +x "$here/tools/scrcpy/scrcpy" "$here/tools/scrcpy/adb" 2>/dev/null || true

# Desktop entry spec: inside a quoted Exec value, \ " ` $ must be escaped.
exec_path="$(printf '%s' "$here/MirrorHub" | sed -e 's/[\\"`$]/\\&/g')"

mkdir -p "$apps"
cat > "$entry" <<EOF
[Desktop Entry]
Type=Application
Name=MirrorHub
Comment=Mirror Android phones and iPhones/iPads on this PC
Exec="$exec_path"
Icon=$here/app.png
Terminal=false
Categories=AudioVideo;Video;Utility;
Keywords=scrcpy;AirPlay;Android;iPhone;mirror;
StartupWMClass=MirrorHub
EOF
command -v update-desktop-database >/dev/null && update-desktop-database "$apps" 2>/dev/null || true
echo "MirrorHub added to the applications menu."

# What is still missing for the iPhone/iPad part (Android works with the bundled scrcpy and adb).
if ! command -v uxplay >/dev/null; then
  echo
  echo "For iPhone/iPad mirroring install UxPlay, e.g. on Debian/Ubuntu:"
  echo "  sudo apt install uxplay gstreamer1.0-plugins-bad gstreamer1.0-libav gstreamer1.0-gl avahi-daemon"
fi
if command -v systemctl >/dev/null && ! systemctl is-active --quiet avahi-daemon 2>/dev/null; then
  echo
  echo "avahi-daemon is not running (the iPhone finds this PC through it):"
  echo "  sudo systemctl enable --now avahi-daemon"
fi
