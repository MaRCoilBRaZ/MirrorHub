# Third-party software

MirrorHub is licensed under the GNU General Public License v3.0 (see `LICENSE.txt`).
The Linux release bundles the following third-party software, each under its own license.

| Component | Where | License | Source |
|---|---|---|---|
| scrcpy (with adb, FFmpeg, SDL3, libusb) | `tools/scrcpy/` | Apache-2.0 (scrcpy); see `tools/scrcpy/LICENSE` and the respective projects | https://github.com/Genymobile/scrcpy |
| Avalonia UI, with SkiaSharp (Skia), HarfBuzzSharp (HarfBuzz), MicroCom and Tmds.DBus | inside `MirrorHub` | MIT (Avalonia, SkiaSharp, HarfBuzzSharp, MicroCom, Tmds.DBus); BSD-3-Clause (Skia); Old MIT (HarfBuzz) | https://github.com/AvaloniaUI/Avalonia |
| QRCoder | inside `MirrorHub` | MIT | https://www.nuget.org/packages/QRCoder |
| .NET runtime | inside `MirrorHub` | MIT | https://github.com/dotnet/runtime |

UxPlay (iPhone/iPad mirroring) is not bundled: MirrorHub runs the `uxplay` installed on the system.

AirPlay, iPhone and iPad are trademarks of Apple Inc. Android is a trademark of Google LLC.
MirrorHub is not affiliated with or endorsed by Apple, Google or the projects above.
