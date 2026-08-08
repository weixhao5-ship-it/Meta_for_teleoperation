[한국어](README_KOR.md) | **English**

# Meta_for_teleoperation

**Unity app for Meta Quest (Horizon OS).** Visualizes hand tracking on top of
the passthrough view and streams the HMD, both hands (21 landmarks) and both
controllers' pose/input state to a PC over TCP in real time, every frame.
The headset is the server; the PC is the client.

```
Meta Quest (Unity APK)  ──TCP :5555 (NDJSON)──▶  PC (subscriber / robot control, etc.)
```

## Transport

| Item | Value |
| --- | --- |
| Protocol | **TCP server** — the headset opens the port; the PC connects to `tcp://<headset-ip>:5555` |
| Port | `5555` (configurable in the Inspector) |
| Format | **NDJSON** — one JSON object per line, `\n`-terminated, UTF-8 |
| Rate | **Every render frame** (Quest ≈ 72 Hz). Cap with `maxPublishHz > 0` (default 0 = uncapped) |
| Semantics | **Latest-value broadcast** — a background send thread writes only the most recent frame to every connected client. A slow client never stalls the stream, and broken clients are dropped automatically |
| Delivery guarantee | Frames where nothing is tracked are still published (marked `tracked: false`), never skipped |

The publisher (`PoseFramePublisher`) is already wired into the scene as the
`PosePublisher` object in `SampleScene`, so no manual setup is needed — a
freshly built APK starts streaming immediately. The required permissions
(`INTERNET`, `com.oculus.permission.HAND_TRACKING`) are injected into the
manifest automatically at build time.

**Receive test** (PC, standard library only):

```bash
python Tools/subscriber.py --host <headset-ip> --port 5555
```

Find the headset IP in the device's Wi-Fi settings; it must be on the same
network as the PC.

## Data schema

One line = one frame, consisting of `timestamp` / `frame` / `hmd` /
`hands` (21 landmarks each) / `controllers`. Field definitions, coordinate
system and omission rules:

**→ [SCHEMA.md](SCHEMA.md)**

(The schema is **identical** to the Galaxy_with_hands repository — receiver
code can be reused for either headset without any changes.)

## Device mapping

Which physical input becomes which JSON field:

![HMD / controller key mapping](docs/images/meta_keymap.jpg)

## Building the APK

### Required installs (on your machine)

| Requirement | Notes |
| --- | --- |
| **Unity Editor `6000.2.12f1`** | This project's pinned version (install via Unity Hub) |
| **Android Build Support module** | Add to the editor above via the Hub. Bundles OpenJDK + Android SDK/NDK, so nothing else is needed |
| **`adb`** (Android platform-tools) | Needed to install the APK on the device and view logs (not needed for the build itself). On Ubuntu/Debian: `sudo apt install adb`. Alternatively add the copy bundled with Android Build Support (`<Editor>/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`) to your PATH |

All other project configuration (packages, scenes, XR settings, permissions)
is **included in the repo** and restores automatically when you open the
project in Unity after cloning. After the first package import, check once:
**Project Settings ▸ XR Plug-in Management ▸ Android** — the `OpenXR` loader
plus the `Hand Tracking Subsystem` + `Meta Quest Support` +
`AR Session`/`AR Camera` (Meta) features must be enabled.

### How to build

- **Editor menu**: `Build ▸ Android APK` → `Build/<productName>.apk`
- **Batch mode** (no editor UI):
  ```bash
  Unity -quit -batchmode -nographics -projectPath . \
        -buildTarget Android -executeMethod BuildAndroid.Build \
        -output Build/Meta_with_hands.apk
  ```
- **Both APKs at once** (recommended):
  ```bash
  UNITY=~/Unity/Hub/Editor/6000.2.12f1/Editor/Unity ./Tools/build_all.sh
  # → Build/Meta_with_hands.apk        (com.dongridong.metahand)
  # → Build/Meta_with_controllers.apk  (com.dongridong.metacontroller)
  ```
  The two APKs have different package names, so they install **side by side**
  on the headset. Product identity is injected via build arguments
  (`-productName` / `-applicationIdentifier`) and restored after the build,
  so `ProjectSettings.asset` never gets dirty.

### Installing on the device

1. Enable **Developer Mode** on the Quest (via the Meta Horizon mobile app),
   have `adb` ready on the PC, and connect over USB (or wireless adb).
2. With no signing key configured, a **debug signature is applied
   automatically** → no extra key is needed for sideloading.
   ```bash
   adb install -r Build/Meta_with_hands.apk
   ```
3. On first launch, allow the **hand tracking permission** dialog.

## Sister repository

The **Samsung Galaxy XR (Android XR) edition** with the same schema and
layout: `Galaxy_with_hands` (github.com/dongridong/Galaxy_with_hands).
Only the Unity XR configuration (plugins/packages) differs per device;
the transport and wire format are identical.
