# MirrorHub

**Mirror and control your Android phone, and receive AirPlay from your iPhone/iPad, on Windows and Linux. Free, open source, no app needed on the phone.**

MirrorHub is a desktop app for Windows and Linux that brings together two excellent open-source engines behind one simple interface:

- **Android** → [scrcpy](https://github.com/Genymobile/scrcpy): low-latency mirroring over USB or Wi-Fi, with audio and full mouse/keyboard control.
- **iPhone / iPad** → [UxPlay](https://github.com/FDH2/UxPlay): an AirPlay screen-mirroring receiver.

## Features

**Android**
- Mirroring over USB or Wi-Fi, H.264 / H.265 / AV1, hardware decoding
- Simple **quality presets** (Balanced, Low latency, Best quality, Weak Wi-Fi); bitrate, FPS, resolution and buffers under *Advanced settings*
- Audio forwarding (Android 11+) and mouse/keyboard control
- **Audio only** mode (Android 11+): the phone's sound on the PC with no video and no window, very light, to keep on in the background (e.g. while gaming with headphones)
- **Sound on both** (Android 13+): keep the sound on the phone too while it also plays on the PC. On older Android versions the option leaves the sound on the device
- Wireless setup: pair with a **QR code** or a 6-digit code (Android 11+), or switch a USB device to Wi-Fi in one click
- **Device profiles**: different settings per phone, with optional **auto-mirror on connect**
- Auto-reconnect with backoff if the connection drops
- Recording (`.mkv`) and screenshots
- Tools: install APKs, send files (also by drag & drop), launch a specific app
- **Phone camera** window (Android 12+): front/back camera, zoom, rotation, mirror, microphone, torch, recording; usable as a webcam through OBS

**iPhone / iPad**
- AirPlay screen mirroring receiver with audio, up to 4K (H.265)
- **Audio only** mode: the iPhone's sound on the PC with no video window (Screen Mirroring, or the AirPlay icon of music/video apps)
- **Several iPhones/iPads at the same time** (up to 4): each receiver has its own name (MirrorHub, MirrorHub 2…) and its own window
- **Sound delay** to line up sound and picture, and a **lowest latency** mode for games
- Quality presets (Full HD, 1440p, 4K), optional PIN, custom name, D3D12/D3D11 rendering, `.mp4` recording
- Checks before starting: on Windows, if the network is Public or the firewall blocks the receiver, it offers to fix it (one administrator prompt); on Linux it checks avahi-daemon and firewalld and shows the commands to run
- Clear status and error messages; falls back to the automatic renderer if the chosen one does not work on the PC

**App**
- Notification-area / system-tray icon with a menu to control everything, including **Restart**
- Start with Windows / at login, start minimized, close to tray, single instance
- Notifications you can turn on/off per event
- **Customizable global keyboard shortcuts** (Windows); on Linux, bind `MirrorHub --command ...` in your desktop's keyboard settings (see below)
- **Copy log** button; the log is also saved to `%AppData%\MirrorHub\mirrorhub.log` (Linux: `~/.config/MirrorHub/mirrorhub.log`)
- Leaves nothing running on exit: scrcpy, UxPlay and adb end together with MirrorHub (on Windows even if it is closed from Task Manager)

## Download

Get the latest `MirrorHub-vX.Y.Z-win-x64.zip` from [Releases](../../releases), extract it anywhere and run `MirrorHub.exe`.
Everything is included (scrcpy, adb, UxPlay, GStreamer): nothing to install. Windows 10/11 x64.

> Windows SmartScreen may warn about an unrecognized app because the executable is not code-signed: click **More info → Run anyway**.

**Linux** (x86-64, X11 or Wayland): get `MirrorHub-vX.Y.Z-linux-x64.tar.gz`, then

```bash
tar -xzf MirrorHub-vX.Y.Z-linux-x64.tar.gz
./MirrorHub/install.sh     # optional: adds MirrorHub to the applications menu
./MirrorHub/MirrorHub
```

scrcpy and adb are included. For iPhone/iPad install UxPlay from your distribution, e.g. on Debian/Ubuntu:
`sudo apt install uxplay gstreamer1.0-plugins-bad gstreamer1.0-libav gstreamer1.0-gl avahi-daemon`
(UxPlay 1.70+ for H.265, 1.73+ for recording).

## Quick start

**Android**
1. On the phone, enable **Developer options** (tap *Build number* 7 times) and turn on **USB debugging**.
2. Connect it with a USB cable and accept the prompt on the phone, or use **Pair with QR…** for Wi-Fi.
3. Select the device and click **▶ Start mirroring**.

**iPhone / iPad**
1. PC and iPhone on the same Wi-Fi network, set as a **Private** network in Windows.
2. Tab **iPhone / iPad** → **▶ Start receiver**. The first time, accept the offer to set up the network/firewall (administrator prompt).
3. On the iPhone: **Control Center → Screen Mirroring → MirrorHub**.

For sound only, use **♪ Audio only** instead of Start receiver. For more iPhones/iPads at once, set *iPhones/iPads at the same time* under Options: each device picks a different name.

Shortcuts inside the Android mirror window come from scrcpy, e.g. `Alt+F` fullscreen, `Alt+H` home, `Alt+B` back, `Alt+O` turn the phone screen off.

**On Linux**
- USB: install the Android udev rules (Debian/Ubuntu: `sudo apt install android-sdk-platform-tools-common`), then replug the phone; otherwise it shows as "no permissions".
- iPhone/iPad: `avahi-daemon` must be running. With a firewall, allow mDNS and the receiver ports 7000-7011 (TCP and UDP), e.g. `sudo ufw allow 5353/udp && sudo ufw allow 7000:7011/tcp && sudo ufw allow 7000:7011/udp`.
- Tray icon: on GNOME it needs the *AppIndicator and KStatusNotifierItem Support* extension (included in Ubuntu). Without a tray, closing the window quits MirrorHub.

## Command line

A running MirrorHub can be controlled from scripts or from your desktop's keyboard shortcuts:

```
MirrorHub --command <show | toggle-window | toggle-mirror | toggle-audio-only | toggle-airplay | toggle-airplay-audio | toggle-camera | screenshot | exit>
```

If MirrorHub is not running yet, it starts and then runs the command. On Linux, where apps cannot register system-wide shortcuts, *Settings* lists these commands ready to copy.

## Build from source

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The UI is [Avalonia](https://avaloniaui.net/); one project builds both versions.

```bash
git clone https://github.com/MaRCoilBRaZ/MirrorHub.git
cd MirrorHub
dotnet run -c Release -f net8.0-windows   # Windows
dotnet run -c Release -f net8.0           # Linux
```

At runtime MirrorHub looks for the external tools in this order:

1. `tools/` next to the executable (the layout of the release packages)
2. `PATH` (e.g. `winget install Genymobile.scrcpy`, or your Linux package manager)
3. Windows only: the WinGet package folder, and `C:\msys64\ucrt64\bin\uxplay.exe` for UxPlay built with MSYS2

To build UxPlay yourself (MSYS2 UCRT64 shell):

```bash
pacman -S --needed git mingw-w64-ucrt-x86_64-{cmake,gcc,ninja,pkgconf,libplist,openssl,gstreamer,gst-plugins-base,gst-plugins-good,gst-plugins-bad,gst-libav}
git clone https://github.com/FDH2/UxPlay.git && cd UxPlay
cmake -B build -G Ninja && ninja -C build && cmake --install build --prefix /ucrt64
```

`packaging/bundle-uxplay.sh` turns that build into a self-contained folder (exe, DLLs and only the GStreamer plugins UxPlay uses).
The [release workflow](.github/workflows/release.yml) does all of this on every `v*` tag, builds the Linux package too (with a start-up smoke test) and publishes both.

## Project layout

| Path | What |
|---|---|
| `MainWindow*.cs`, `*.axaml` | UI (Avalonia): Android, iPhone/iPad and Settings tabs, tray menu, hotkeys |
| `Program.cs`, `AppCommands.cs` | Single instance and the `--command` channel (named pipe) |
| `Platform/Windows/`, `Platform/Linux/` | Everything OS-specific behind `Os`: tray, global hotkeys, autostart, child-process cleanup, AirPlay network checks |
| `Scrcpy.cs`, `Adb.cs` | Android: scrcpy arguments, adb commands, app and camera listing |
| `UxPlay.cs` | iPhone/iPad: UxPlay arguments and environment |
| `WatchedProcess.cs` | Runs scrcpy/UxPlay with a watchdog (restart with exponential backoff) |
| `Settings.cs` | Settings, device profiles, quality presets (`%AppData%\MirrorHub\settings.json`, Linux `~/.config/MirrorHub`) |
| `packaging/` | UxPlay bundling script, Linux install script, release readmes, third-party notices |

## License

MirrorHub is released under the [GNU GPL v3.0](LICENSE).
Release packages also contain third-party software under their own licenses: see [THIRD-PARTY-NOTICES](packaging/THIRD-PARTY-NOTICES.md).

AirPlay, iPhone and iPad are trademarks of Apple Inc.; Android is a trademark of Google LLC.
MirrorHub is an independent project, not affiliated with Apple, Google, scrcpy or UxPlay. All the heavy lifting is done by
[scrcpy](https://github.com/Genymobile/scrcpy), [UxPlay](https://github.com/FDH2/UxPlay) and [GStreamer](https://gstreamer.freedesktop.org/): thanks to their authors.
