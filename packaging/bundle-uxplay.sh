#!/usr/bin/env bash
# Builds a self-contained UxPlay folder (uxplay.exe + DLLs + the GStreamer plugins it uses)
# from an MSYS2 UCRT64 installation. Run inside an MSYS2 UCRT64 shell.
#
#   packaging/bundle-uxplay.sh <path/to/uxplay.exe> <output-dir>
#
# Layout (GStreamer finds plugins relative to its own DLL: <bin>/../lib/gstreamer-1.0):
#   <output-dir>/bin/uxplay.exe + DLLs
#   <output-dir>/lib/gstreamer-1.0/*.dll
#   <output-dir>/libexec/gstreamer-1.0/gst-plugin-scanner.exe
set -euo pipefail

exe="$1"
out="$2"
prefix="${MINGW_PREFIX:-/ucrt64}"

# Elements used by UxPlay's audio/video pipelines on Windows (see renderers/*.c).
plugins=(
  coreelements app playback typefindfunctions pbtypes          # core, appsrc, decodebin
  videoparsersbad audioparsers                                  # h264parse, h265parse, aacparse
  libav                                                         # avdec_h264, avdec_aac, avdec_alac
  d3d11 d3d12 d3d nvcodec                                       # hardware decoders and video sinks
  videoconvertscale videorate videofilter autodetect            # videoconvert, videoscale, videoflip, autovideosink
  audioconvert audioresample volume level                       # audio path
  wasapi wasapi2 directsound                                    # audio sinks
  isomp4                                                        # mp4mux (-mp4 recording)
  jpeg imagefreeze pango                                        # cover-art pipeline
)

rm -rf "$out"
mkdir -p "$out/bin" "$out/lib/gstreamer-1.0" "$out/libexec/gstreamer-1.0"

cp "$exe" "$out/bin/"
for p in "${plugins[@]}"; do
  cp "$prefix/lib/gstreamer-1.0/libgst$p.dll" "$out/lib/gstreamer-1.0/"
done
cp "$prefix/libexec/gstreamer-1.0/gst-plugin-scanner.exe" "$out/libexec/gstreamer-1.0/"

# Every DLL (transitively) needed by the exe, the plugins and the scanner that comes from the MSYS2 prefix.
ldd "$out/bin/uxplay.exe" "$out"/lib/gstreamer-1.0/*.dll "$out/libexec/gstreamer-1.0/gst-plugin-scanner.exe" \
  | awk '{print $3}' | grep -i "^$prefix/bin/" | sort -u \
  | while read -r dll; do cp -n "$dll" "$out/bin/"; done

echo "UxPlay bundle: $out ($(du -sh "$out" | cut -f1), $(ls "$out/bin" | wc -l) files in bin)"
