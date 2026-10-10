# Quest GStreamer H.264/RTP receiver

`ArmCameraStreamManager` on the `Main Camera` listens on fixed UDP ports **5000–5003**. A movable
window appears only after a port delivers a decoded video frame. If frames stop for **2.5 seconds**,
that stream's window closes. The number of active PC streams therefore determines how many Quest
windows appear, without rebuilding the APK for one to four streams.
The Quest pipeline is:

```text
udpsrc → rtpjitterbuffer → rtph264depay → h264parse
       → decodebin (Android MediaCodec hardware decoder only)
       → videoconvert/videoscale → appsink → Unity texture
```

The `decodebin` autoplug callback only allows H.264 decoder factories from `androidmedia` named
`amcviddec-*` whose GStreamer class ends in `/Hardware`. It skips all other H.264 decoder
candidates, so there is no software-decoder fallback. If Quest does not expose a compatible
MediaCodec decoder, receiver startup fails with an error. Decoded frames are converted/scaled to
RGBA for Unity display; this copies pixels on the CPU, but H.264 decoding is done by the Quest
hardware decoder.

## Incoming stream

- RTP version 2, H.264 payload type **96**, 90 kHz clock, RFC 6184 packetization mode 1.
- Send SPS/PPS in-band, preferably repeated with keyframes to allow decoder startup/recovery.
- Set the RTP marker bit at each access-unit/frame boundary.
- Send unicast UDP to the Quest IP address on one of ports **5000–5003**; allow these ports through
  the host/network firewall.
- The stream's encoded resolution may differ from the receiver's output size. `streamWidth` and
  `streamHeight` on `ArmCameraStreamManager` configure the RGBA texture dimensions; GStreamer
  scales decoded frames to this size. Defaults are **1280×720**.

## Multiple streams

The Android build contains four isolated receiver slots. Quest starts a GStreamer pipeline for each
port in the fixed pool, with a separate Android MediaCodec hardware decoder and RGBA frame buffer.
Windows are laid out automatically: one centered, two side by side, or three to four in a grid.
They face the headset's horizontal heading and stay upright relative to world up, even when the
wearer looks up/down or tilts their head. Each window remains spatially anchored and can be dragged
with a Quest controller.

Use a matching fixed port for each PC camera stream. One stream opens one window:

```bash
python src/organza_robot/leader/run_rebot_wist.py \
  --host 192.168.0.207 \
  --video-stream /dev/video0:5000
```

Two streams open two windows. Up to four streams can run simultaneously:

```bash
python src/organza_robot/leader/run_rebot_wist.py \
  --host 192.168.0.207 \
  --video-stream /dev/video0:5000 \
  --video-stream /dev/video1:5001
```

Choose **Mono** on `ArmCameraStreamManager` to show one image in both eyes, or **Side By Side** when
the incoming frame packs left/right views horizontally. Windows are spatially anchored after they
appear and can be moved with a controller. Passthrough is off by default;
`PassthroughSetup.showPassthrough` can restore passthrough behind virtual content.

## Android build

- GStreamer Android SDK **1.28.7**, NDK build integration, and the matching `arm64-v8a` runtime
  libraries are required. `androidmedia`, `rtp`, `rtpmanager`, `udp`, `videoparsersbad`,
  `videoconvertscale`, and `playback` are linked into the packaged GStreamer runtime.
- `GStreamer.init(Context)` and GStreamer's Android MediaCodec callback classes are included under
  `Assets/Plugins/Android/org/freedesktop/gstreamer`. Font configuration and CA certificates are
  packaged in `Assets/Plugins/Android/libs/gstreamer-assets.aar` so they are available through
  Android's `AssetManager` at runtime.
- The Unity native bridge is built for **arm64-v8a** and uses **OpenGLES 3** to upload appsink
  frames to a Unity-owned texture. The Android graphics API list is configured for OpenGLES 3.
- Android Internet permission is enabled for UDP reception.
- The hand/pose publisher uses its separate UDP registration and data port 5555.

The native build sources are in `Tools/gstreamer`. Rebuilding requires the official GStreamer
Android SDK **1.28.7** `arm64` directory and Unity's Android NDK. The Unity project minimum Android
API is 32; `Application.mk` uses the same API level. After rebuilding the primary native plugin,
run `Tools/gstreamer/CreateMultiStreamPluginCopies.ps1` to refresh the three isolated receiver
library copies.
