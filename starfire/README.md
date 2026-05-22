# STARFIRE — ADEPTUS ASTARTES AR HUD

> *"The Emperor protects, but the Cogitator sees all."*

A wearable Warhammer 40k Space Marine HUD spanning four machines, designed to
gracefully degrade across three operational levels as connectivity changes.

```
                           ┌──────────────────────────────────────────────┐
                           │      COGITATOR PRIME  (Desktop PC, home)     │
                           │   RX 6600 XT • YOLO + MiDaS + MediaPipe      │
                           │   ws://starfire.yourdomain.com:8765 (DDNS)   │
                           └────────────────────────┬─────────────────────┘
                                                    │  WiFi / DDNS
                                                    │
                           ┌────────────────────────┴─────────────────────┐
                           │       BACKPACK RELAY  (Laptop, on back)      │
                           │       zero-ML • <20MB • mDNS broker          │
                           │       starfire.local  /  0.0.0.0:8765        │
                           └──────┬──────────────────────────┬────────────┘
                                  │ USB-C  (adb reverse)     │ USB-tether
                                  │                          │
                ┌─────────────────┴────────────┐  ┌──────────┴───────────┐
                │     HUD PRIME  (Quest 2)     │  │  ASTARTES SENSOR     │
                │   helmet-mounted, passthrough│  │  (Realme C55, Termux)│
                │   ws://localhost:8765        │  │  GPS + accelerometer │
                └──────────────────────────────┘  │  IP Webcam @:8080    │
                                                  └──────────────────────┘
```

The system runs at three levels — **switched automatically, no restart**:

| Level | Reachability                  | HUD         | ML                |
| :---: | :---------------------------- | :---------- | :---------------- |
| 1     | Quest → laptop → PC (DDNS)    | green       | YOLO+MiDaS+Face   |
| 2     | Quest → laptop, PC unreachable| amber       | edge-detect only  |
| 3     | Quest standalone              | amber       | edge-detect only  |

---

## 1. Prerequisites

| Device      | Required                                                               |
| ----------- | ---------------------------------------------------------------------- |
| Desktop PC  | Windows 11, Python 3.10–3.12, AMD GPU drivers (RX 6600 XT)             |
| Laptop      | Windows 11, Python 3.10–3.12, `adb` (Android platform-tools on PATH)   |
| Phone       | Realme C55 / any Android 10+, Termux from F-Droid, IP Webcam app       |
| Quest 2     | v68+ firmware, Developer Mode on, USB-C cable                          |
| Unity (dev) | Unity 6000.0.x LTS, Android Build Support module, Meta Quest Dev Hub   |

## 2. Setup order

1. **PC**: clone repo → `cd starfire` → `setup_pc.bat`.
2. **Laptop**: copy `starfire/` folder → `setup_laptop.bat`.
3. **Phone**: install Termux (F-Droid) + Termux:API + IP Webcam → run `bash setup_termux.sh`.
4. **Quest**: build the Unity project, sideload APK via Meta Quest Developer Hub.

## 3. Find local IPs

| Device   | Command                                                            |
| -------- | ------------------------------------------------------------------ |
| Windows  | `ipconfig` — look for "IPv4 Address" under your active adapter     |
| Termux   | `ip addr show` or `ifconfig`                                       |
| Android  | Settings → About phone → Status → IP address                        |
| Quest    | Settings → Wi-Fi → tap your network → Advanced                      |

## 4. Configure `config.json`

Edit `starfire/config.json`:

- `pc_domain`: your DDNS hostname (e.g. `starfire.duckdns.org`). Plain LAN IP
  also works when you're at home.
- `pc_port`: TCP port forwarded to the PC (default `8765`).
- `phone_stream_ip`: the phone's IP on your USB-tether subnet (usually
  `192.168.42.129` on Realme C55).
- The remaining fields are well-named defaults — leave them alone unless you
  know why you're changing them.

## 5. DDNS for the PC

Recommended: **DuckDNS** (free).

1. Sign in at https://www.duckdns.org with a Google/GitHub account.
2. Pick a subdomain, e.g. `starfire`.
3. Install the DuckDNS updater (Windows scheduled task on the PC).
4. Forward TCP port **8765** on your home router → PC's LAN IP.
5. Set `pc_domain` to `starfire.duckdns.org`.

## 6. IP Webcam on the phone

1. Install **IP Webcam** by Pavel Khlebovich from Google Play.
2. Open the app, scroll to the bottom → **Start server**.
3. App reports the stream URL — typically `http://<phone-ip>:8080`.
4. Verify `http://<phone-ip>:8080/video` plays in your laptop's browser.

## 7. USB tethering on Realme C55

Settings → Connection & sharing → Personal hotspot → **USB tethering** = ON.
Plug into laptop; Windows assigns the phone an IP like `192.168.42.129`.

## 8. Termux

Install from **F-Droid** (the Play Store version is dead — no API updates).

```
pkg update -y && pkg install -y python termux-api
pip install websockets
```

Then **install the Termux:API app** as well from F-Droid (the companion app —
without it `termux-location` and `termux-sensor` won't work).

## 9. Meta Quest Developer Hub

Download from https://developer.oculus.com/downloads/package/oculus-developer-hub-win
Sign in with the same account as your headset. The Hub bundles `adb`.

## 10. Enable Developer Mode on Quest 2

In the Meta Quest mobile app: Devices → your Quest → Developer Mode → ON.
You may have to create a developer organization first (it's free).

## 11. Build Unity project for Quest

1. New Unity 6000.0 LTS project (URP template).
2. Drop **all** `.cs` files from `starfire/unity/Scripts/` into `Assets/Scripts/`.
3. Drop both `.shader` files from `starfire/unity/Shaders/` into `Assets/Shaders/`.
4. Install packages (Package Manager):
   - **Oculus XR Plugin** (`com.unity.xr.oculus`)
   - **XR Plugin Management** (`com.unity.xr.management`) — enable Oculus loader on Android
   - **Newtonsoft Json** (`com.unity.nuget.newtonsoft-json`)
   - **TextMeshPro** (built in)
   - **NativeWebSocket** — Window → Package Manager → `+` → "Add from git URL" →
     `https://github.com/endel/NativeWebSocket.git#upm`
   - **Meta XR All-in-One SDK** from the Asset Store
5. Project Settings:
   - Platform: **Android** (File → Build Settings → Switch Platform)
   - Player → Graphics API: **Vulkan only**
   - Player → Min API Level: **32**, Target API Level: **32**
   - Player → Other → Color Space: **Linear**
   - Player → Other → Permissions: add `android.permission.RECORD_AUDIO`
   - XR Plug-in Management → Android tab → Oculus checked
   - Oculus tab → Stereo Rendering Mode: Multiview, Low Overhead Mode ON,
     Hand Tracking Support = **Controllers and Hands**, Passthrough Support = **Required**.
6. Scene layout: see the **Scene** section below.
7. Build & Run with Quest connected.

### Scene

- `OVRCameraRig` (from the SDK).
  - `TrackingSpace/CenterEyeAnchor`
    - Add `OVRPassthroughLayer` component, Placement = **Underlay**, assign a
      material that uses `Shaders/IRColorShader.shader`.
    - Child `HUDCanvas` (World Space canvas), z = 1.2 m, scale 0.001.
      - `HelmetFrameOverlay` (image, L-shaped corner brackets)
      - `TopLeftPanel`, `TopRightPanel`, `CenterReticle`, `DetectionPanel`,
        `DepthIndicator`, `FacePanel`, `CompassBar`, `NavLinkStatus`,
        `NetworkMapPanel`, `FPSDebugOverlay`, `FlavorTextPanel`.
      - `InputBus` (empty GameObject) with `InputBus.cs`, `GestureDetector.cs`,
        `VoiceCommand.cs`.
  - `TrackingSpace/LeftHandAnchor` and `RightHandAnchor`: add
    `OVRHand`, `OVRSkeleton`, `OVRMeshRenderer`, `HandVisualizer.cs`.
- A root `MeshNode` GameObject holds `MeshNode.cs`.
- A root `HUDController` GameObject holds `HUDController.cs` referencing all
  panels by inspector field.

## 12. Sideload APK via Meta Quest Developer Hub

In MQDH: **Device Manager → Apps → +Add Build → Install APK**. The APK lands
on the headset under "Unknown Sources".

## 13. Hand tracking & voice — *no controllers needed*

Right after launch:

| Action                       | Gesture                        | Voice                       |
| ---------------------------- | ------------------------------ | --------------------------- |
| Toggle FPS diagnostics       | Right fist, hold 1 s           | "Astartes diagnostics"      |
| Toggle network (augur) map   | Left fist, hold 1 s            | "Astartes augur"            |
| Cycle panels (next / prev)   | Right pinch / left pinch       | —                           |
| Acknowledge alert            | Right thumbs up                | —                           |
| Dismiss / clear              | Open palm                      | "Astartes clear"            |
| Lock reticle on target       | Right two-finger point         | "Astartes target"           |
| Hide / reveal HUD            | Left wrist raise               | "Astartes hide" / "reveal"  |
| Read detections aloud        | —                              | "Astartes report"           |
| Read NAV LINK status aloud   | —                              | "Astartes mode"             |
| Easter egg                   | —                              | "Astartes Emperor"          |

Wake-words: **Astartes**, **Brother**, or **Aquila** — followed by the
command within 4 s.

## 14. Session startup procedure

Every time you put on the helmet:

1. On the PC: double-click `start_pc.bat`. Wait for `[YOLO]` / `[DEPTH]` /
   `[FACE]` lines to appear in the console.
2. Plug the phone into the laptop with USB. Enable **USB tethering**. Start
   the IP Webcam app on the phone → **Start server**.
3. Plug the Quest into the laptop with USB-C (no Link required).
4. On the laptop: double-click `start_laptop.bat`. It establishes the ADB
   reverse tunnel, prints the network map, and starts the relay.
5. On the phone, open Termux: `cd starfire && python sensor_node.py`.
6. Put on the helmet and launch the **Starfire** app from "Unknown Sources"
   on the Quest.
7. Verify the HUD shows green `● COGITATOR LINK: ...` within ~3 s.

## 15. Troubleshooting

**NAV LINK LOST on Quest immediately.**
The Quest can't reach `localhost:8765`. Re-run `adb reverse tcp:8765 tcp:8765`
in a laptop terminal. Confirm with `adb devices` that the Quest is listed.

**ADB tunnel not working.**
```
adb kill-server
adb start-server
adb reverse tcp:8765 tcp:8765
```
If `adb devices` still shows nothing, accept the USB-debug prompt inside the
headset (look around — it's a tiny dialog).

**DirectML not detected.**
`python -c "import onnxruntime as ort; print(ort.get_available_providers())"`
should list `DmlExecutionProvider`. If you see only `AzureExecutionProvider`
and `CPUExecutionProvider`, a plain `onnxruntime` (or `onnxruntime-azure`)
is shadowing the DirectML wheel — both packages install into the same
namespace so only one wins. Reinstall cleanly:
```
pip uninstall -y onnxruntime onnxruntime-azure onnxruntime-gpu
pip install onnxruntime-directml
```
Then make sure your AMD GPU drivers are current. `setup_pc.bat` performs
this uninstall automatically, but only if you re-run it after pulling.

**Python 3.13 — `module 'mediapipe' has no attribute 'solutions'`.**
Mediapipe (and `onnxruntime-directml`) don't yet publish Python 3.13
wheels. `setup_pc.bat` refuses to run on 3.13; install Python 3.10, 3.11,
or 3.12. The server also tolerates a stub mediapipe at runtime — it will
print "face detection disabled" and keep YOLO / depth running.

**`starfire.local` not resolving.**
On Windows, install **Bonjour Print Services** (Apple) to enable mDNS.
On Android/Quest, mDNS works out of the box. As a fallback, point Unity
directly at the laptop IP (you can hand-edit `MeshNode.cs` — the `ws://`
URI uses ADB tunnel so it's only relevant when not tunnelled).

**IP Webcam stream not found.**
Check that the IP Webcam app is foreground; some Android battery savers
kill it. Open `http://<phone-ip>:8080/video` in a laptop browser to verify.

**Quest passthrough black screen.**
Make sure `OVRPassthroughLayer.placement` is **Underlay** (not Overlay),
that the layer is enabled, and that passthrough is enabled in Quest system
settings (Settings → Physical space).

**Shader not applying to passthrough layer.**
`OVRPassthroughLayer.edgeRenderingEnabled` must be ON for the SDK to bind
your texture. Drop the material onto `Style Transfer → Texture` slot on
the layer component.

**YOLO model download failing.**
Manually download a YOLOv8l ONNX export and place it next to `server.py`
as `yolov8l.onnx`. Or run `yolo export model=yolov8l.pt format=onnx` in a
Python shell.

**Phone GPS not working in Termux.**
You must install **both** Termux and **Termux:API** from F-Droid (separate
APKs). Grant location permission to Termux:API.

---

*Ave Imperator.*
