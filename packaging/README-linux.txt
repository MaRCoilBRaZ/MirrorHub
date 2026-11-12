MirrorHub for Linux - mirror your Android phone and iPhone/iPad on this PC
==========================================================================

Needs: 64-bit x86 Linux with a desktop (X11, or Wayland with XWayland).

START
1. Extract the archive:   tar -xzf MirrorHub-<version>-linux-x64.tar.gz
2. Optional: run ./MirrorHub/install.sh to add MirrorHub to the applications menu
   (./MirrorHub/install.sh --uninstall removes it again).
3. Start ./MirrorHub/MirrorHub, or MirrorHub from the applications menu.
Keep the whole folder together: MirrorHub uses scrcpy and adb from the "tools" folder.

ANDROID
1. On the phone: Settings -> About phone -> tap "Build number" 7 times
   to unlock Developer options, then enable "USB debugging".
2. USB: Linux needs udev rules for Android phones, otherwise the phone shows as
   "no permissions". Debian/Ubuntu: sudo apt install android-sdk-platform-tools-common
   (other distributions: the android-udev rules package). Then unplug and plug the phone again
   and accept the authorization prompt on the phone.
   Wireless: use "Pair with QR..." (Android 11+) or "USB -> Wi-Fi".
3. Select the device and click "Start mirroring".
   Audio forwarding needs Android 11+, the camera feature needs Android 12+.

IPHONE / IPAD
UxPlay is not included: install it with the package manager. Debian/Ubuntu:
   sudo apt install uxplay gstreamer1.0-plugins-bad gstreamer1.0-libav gstreamer1.0-gl avahi-daemon
   sudo systemctl enable --now avahi-daemon
UxPlay 1.70 or newer is needed for H.265, 1.73 or newer for recording.
1. PC and iPhone on the same Wi-Fi network.
2. If a firewall is active, allow mDNS and the receiver ports (7000-7011):
   ufw:        sudo ufw allow 5353/udp && sudo ufw allow 7000:7011/tcp && sudo ufw allow 7000:7011/udp
   firewalld:  sudo firewall-cmd --permanent --add-service=mdns --add-port=7000-7011/tcp --add-port=7000-7011/udp
               sudo firewall-cmd --reload
3. In MirrorHub, tab "iPhone / iPad": click "Start receiver".
4. On the iPhone: Control Center -> Screen Mirroring -> MirrorHub.

KEYBOARD SHORTCUTS
On Linux an app cannot register system-wide shortcuts by itself (Wayland does not allow it).
The Settings tab lists commands such as  "/path/to/MirrorHub" --command toggle-mirror :
add them as custom shortcuts in your desktop's keyboard settings.

TRAY ICON
MirrorHub keeps running in the system tray. On GNOME this needs the extension
"AppIndicator and KStatusNotifierItem Support" (already included in Ubuntu). Without a tray,
closing the window quits MirrorHub.

PROBLEMS?
Click "Copy log" (bottom right) and paste it in a message to the developer.

Settings and the log are stored in ~/.config/MirrorHub. More: https://github.com/MaRCoilBRaZ/MirrorHub
