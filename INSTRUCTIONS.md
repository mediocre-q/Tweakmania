# Tweakmania setup and update guide

## Before you start

- Tweakmania is for the 32-bit DirectInput version of Trackmania United Forever (TMUF) on Windows.
- TMLoader is optional. On first start, Tweakmania looks for the highest installed TMLoader version and uses its `TmForever.exe`. If it cannot find one, it falls back to the original TMUF installation. You can choose another game executable in **Setup…**.
- Your controller must be available through DirectInput. Tweakmania does not change XInput controllers or system-wide controller settings.

## First-time setup

1. Download the latest portable ZIP from the [GitHub releases page](https://github.com/mediocre-q/Tweakmania/releases) and extract the entire ZIP to a folder. Keep `native` beside `Tweakmania.exe`; the game proxy is inside that folder.
2. Start `Tweakmania.exe`. Check that the detected game path points to the TMUF executable you intend to play. Use **Setup…** to browse to `TmForever.exe` if needed.
3. Make sure the game is closed. In **Setup…**, choose **Install proxy**. Tweakmania places its DirectInput proxy beside the selected game executable. Existing in-game controller bindings are preserved.
4. Start or restart TMUF using the same installation you selected. If you use TMLoader, launch that selected TMLoader version.
5. In Tweakmania, select your DirectInput controller and its steering axis, then choose **Apply in game**.
6. Adjust the center and steering endpoints if needed, then shape the response curve. Use **Live preview** while testing. Turn it off afterward to reduce background work. To see the preview while driving, run TMUF in windowed mode or keep Tweakmania open on a second screen.
7. Save profiles you want to keep. Profiles and settings are stored under `%LOCALAPPDATA%\ControllerTweak` and remain available when you update Tweakmania.

### Calibration tips

- **Center** is the stick position treated as straight ahead. Adjust it if the car steers by itself while the stick rests. **Center now** sets it to the stick's current position.
- **Deadzone** ignores tiny movements around the center. Increase it if minor stick drift remains after centering.
- **Saturation** controls how far you need to move the stick to get full steering. Lower it if wear or damage prevents the stick reaching its physical limit.
- **Minimum** and **Maximum** set the left and right calibration endpoints. Values above 100% make full physical travel produce less than full in-game steering; for example, a 125% maximum maps full right travel to 80% steering.
- The **response curve** maps stick travel to steering sent to the game. A point near the lower-left can make small movements near center more precise; the curve then rises more steeply later to preserve full steering at the edge.

## Updating Tweakmania

1. Close TMUF and Tweakmania before updating. The game must be closed to remove or replace its proxy.
2. Download the newest portable ZIP and extract it to a fresh folder. Keep the new `native` folder beside the new `Tweakmania.exe`; do not mix files from different releases.
3. Read the release notes. **If the bundled proxy changed**, start the updated Tweakmania, confirm it points to the same `TmForever.exe`, then open **Setup…** and choose **Remove proxy** followed by **Install proxy**. This replaces the proxy beside the game with the new release's version. Tweakmania only removes a proxy it can verify as its own; it leaves an unrecognized `dinput8.dll` untouched.
4. Start or restart the game after installing the updated proxy. The game must load the new proxy when it starts.

If the release notes say the proxy did not change, you can use the updated Tweakmania without reinstalling the proxy. Your profiles remain under `%LOCALAPPDATA%\ControllerTweak` when you extract a newer release.

## If Windows Defender flags the game or proxy

Only if Defender blocks a file, add the TMUF installation folder you trust to Windows Security's exclusions. Open **Windows Security → Virus & threat protection → Manage settings → Exclusions → Add or remove exclusions → Add an exclusion → Folder**, then choose the game folder. If Defender removed Tweakmania's proxy, restore or install it again from Tweakmania's **Setup…** page.
