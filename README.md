# MirrorHub

**Mirror and control your Android phone, and receive AirPlay from your iPhone/iPad, on Windows. Free, open source, no app needed on the phone.**

MirrorHub is a Windows desktop app that brings together two excellent open-source engines behind one simple interface:

- **Android** → [scrcpy](https://github.com/Genymobile/scrcpy): low-latency mirroring over USB or Wi-Fi, with audio and full mouse/keyboard control.
- **iPhone / iPad** → [UxPlay](https://github.com/FDH2/UxPlay): an AirPlay screen-mirroring receiver.

## Features

**Android**
- Mirroring over USB or Wi-Fi, H.264 / H.265 / AV1, hardware decoding
- Simple **quality presets** (Balanced, Low latency, Best quality, Weak Wi-Fi); bitrate, FPS, resolution and buffers under *Advanced settings*
- Audio forwarding (Android 11+) and mouse/keyboard control
- **Audio only** mode (Android 11+): the phone's sound on the PC with no video and no window, very light, to keep on in the background (e.g. while gaming with headphones)
- Wireless setup: pair with a **QR code** or a 6-digit code (Android 11+), or switch a USB device to Wi-Fi in one click
- **Device profiles**: different settings per phone, with optional **auto-mirror on connect**
- Auto-reconnect with backoff if the connection drops
- Recording (`.mkv`) and screenshots
- Tools: install APKs, send files (also by drag & drop), launch a specific app
- **Phone camera** window (Android 12+): front/back camera, zoom, rotation, mirror, microphone, torch, recording; usable as a webcam through OBS

**iPhone / iPad**
- AirPlay screen mirroring receiver with audio, up to 4K (H.265)
- Quality presets (Full HD, 1440p, 4K), optional PIN, custom name, D3D12/D3D11 rendering, `.mp4` recording
- Checks before starting: if the network is Public or the firewall blocks the receiver, it offers to fix it (one administrator prompt)
- Clear status and error messages; falls back to the automatic renderer if the chosen one does not work on the PC

**App**
- Notification-area icon with a right-click menu to control everything
- Start with Windows, start minimized, close to tray, single instance
- Notifications you can turn on/off per event
- **Customizable global keyboard shortcuts**
- **Copy log** button; the log is also saved to `%AppData%\MirrorHub\mirrorhub.log`
- Leaves nothing running on exit: scrcpy, UxPlay and adb end together with MirrorHub, even if it is closed from Task Manager

## Download

Get the latest `MirrorHub-vX.Y.Z-win-x64.zip` from [Releases](../../releases), extract it anywhere and run `MirrorHub.exe`.
Everything is included (scrcpy, adb, UxPlay, GStreamer): nothing to install. Windows 10/11 x64.

> Windows SmartScreen may warn about an unrecognized app because the executable is not code-signed: click **More info → Run anyway**.

## Quick start

**Android**
1. On the phone, enable **Developer options** (tap *Build number* 7 times) and turn on **USB debugging**.
2. Connect it with a USB cable and accept the prompt on the phone, or use **Pair with QR…** for Wi-Fi.
3. Select the device and click **▶ Start mirroring**.

**iPhone / iPad**
1. PC and iPhone on the same Wi-Fi network, set as a **Private** network in Windows.
2. Tab **iPhone / iPad** → **▶ Start receiver**. The first time, accept the offer to set up the network/firewall (administrator prompt).
3. On the iPhone: **Control Center → Screen Mirroring → MirrorHub**.

Shortcuts inside the Android mirror window come from scrcpy, e.g. `Alt+F` fullscreen, `Alt+H` home, `Alt+B` back, `Alt+O` turn the phone screen off.

## Build from source

Requirements: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/MaRCoilBRaZ/MirrorHub.git
cd MirrorHub
dotnet run -c Release
```

At runtime MirrorHub looks for the external tools in this order:

1. `tools\` next to `MirrorHub.exe` (the layout of the release package)
2. `PATH` (e.g. `winget install Genymobile.scrcpy`)
3. `C:\msys64\ucrt64\bin\uxplay.exe` for UxPlay built with MSYS2

To build UxPlay yourself (MSYS2 UCRT64 shell):

```bash
pacman -S --needed git mingw-w64-ucrt-x86_64-{cmake,gcc,ninja,pkgconf,libplist,openssl,gstreamer,gst-plugins-base,gst-plugins-good,gst-plugins-bad,gst-libav}
git clone https://github.com/FDH2/UxPlay.git && cd UxPlay
cmake -B build -G Ninja && ninja -C build && cmake --install build --prefix /ucrt64
```

`packaging/bundle-uxplay.sh` turns that build into a self-contained folder (exe, DLLs and only the GStreamer plugins UxPlay uses).
The [release workflow](.github/workflows/release.yml) does all of this on every `v*` tag and publishes the zip.

## Project layout

| Path | What |
|---|---|
| `MainWindow*.cs`, `*.xaml` | UI (WPF): Android, iPhone/iPad and Settings tabs, tray icon, hotkeys |
| `Scrcpy.cs`, `Adb.cs` | Android: scrcpy arguments, adb commands, app and camera listing |
| `UxPlay.cs` | iPhone/iPad: UxPlay arguments, environment, network/firewall check and fix |
| `WatchedProcess.cs` | Runs scrcpy/UxPlay with a watchdog (restart with exponential backoff) |
| `ChildProcesses.cs` | Job object that ends every started tool (and the adb server) when MirrorHub exits |
| `Settings.cs` | Settings, device profiles, quality presets (`%AppData%\MirrorHub\settings.json`) |
| `packaging/` | UxPlay bundling script, release readme, third-party notices |

## License

MirrorHub is released under the [GNU GPL v3.0](LICENSE).
Release packages also contain third-party software under their own licenses: see [THIRD-PARTY-NOTICES](packaging/THIRD-PARTY-NOTICES.md).

AirPlay, iPhone and iPad are trademarks of Apple Inc.; Android is a trademark of Google LLC.
MirrorHub is an independent project, not affiliated with Apple, Google, scrcpy or UxPlay. All the heavy lifting is done by
[scrcpy](https://github.com/Genymobile/scrcpy), [UxPlay](https://github.com/FDH2/UxPlay) and [GStreamer](https://gstreamer.freedesktop.org/): thanks to their authors.
