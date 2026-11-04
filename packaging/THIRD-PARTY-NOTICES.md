# Third-party software

MirrorHub is licensed under the GNU General Public License v3.0 (see `LICENSE.txt`).
The portable release bundles the following third-party software, each under its own license.

| Component | Where | License | Source |
|---|---|---|---|
| scrcpy (with adb, FFmpeg, SDL3, libusb) | `tools/scrcpy/` | Apache-2.0 (scrcpy); see `tools/scrcpy/LICENSE.txt` and the respective projects | https://github.com/Genymobile/scrcpy |
| UxPlay | `tools/uxplay/bin/uxplay.exe` | GPL-3.0 (`tools/uxplay/licenses/UxPlay-LICENSE.txt`) | https://github.com/FDH2/UxPlay (exact commit in `tools/uxplay/licenses/SOURCES.txt`) |
| GStreamer, FFmpeg (gst-libav), OpenSSL, libplist and other MSYS2 libraries used by UxPlay | `tools/uxplay/` | LGPL-2.1+, GPL, Apache-2.0 and others, per package | https://github.com/msys2/MINGW-packages (package versions in `tools/uxplay/licenses/SOURCES.txt`) |
| QRCoder | inside `MirrorHub.exe` | MIT | https://www.nuget.org/packages/QRCoder |
| .NET runtime | inside `MirrorHub.exe` | MIT | https://github.com/dotnet/runtime |

AirPlay, iPhone and iPad are trademarks of Apple Inc. Android is a trademark of Google LLC.
MirrorHub is not affiliated with or endorsed by Apple, Google or the projects above.
