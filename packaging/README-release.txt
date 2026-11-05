MirrorHub - mirror your Android phone and iPhone/iPad on Windows
=================================================================

Start: first EXTRACT the zip (right-click -> "Extract All..."), then run MirrorHub.exe
from the extracted folder. Do not open it from inside the zip, and keep the whole folder
together: MirrorHub.exe uses the programs in the "tools" folder.

If Windows shows "Windows protected your PC": click "More info" -> "Run anyway"
(the app is not code-signed).

ANDROID
1. On the phone: Settings -> About phone -> tap "Build number" 7 times
   to unlock Developer options, then enable "USB debugging".
2. Connect the phone with a USB cable and accept the authorization prompt on the phone.
   Wireless: use "Pair with QR..." (Android 11+) or "USB -> Wi-Fi".
3. Select the device and click "Start mirroring".
   Audio forwarding needs Android 11+, the camera feature needs Android 12+.

IPHONE / IPAD
1. PC and iPhone on the same Wi-Fi network, set as "Private" in Windows.
2. In MirrorHub, tab "iPhone / iPad": click "Start receiver". The first time it offers
   to set up the network and the firewall: click Yes and accept the admin prompt.
3. On the iPhone: Control Center -> Screen Mirroring -> MirrorHub.

PROBLEMS?
Click "Copy log" (bottom right) and paste it in a message to the developer.

Settings and the log are stored in %AppData%\MirrorHub. More: https://github.com/MaRCoilBRaZ/MirrorHub
