# Quest GStreamer H.264/RTP receiver

`ArmCameraStreamView` on the `Main Camera` receives the arm-camera stream on UDP port **5000**.
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
- Send unicast UDP to the Quest IP address on port **5000**; allow it through the host/network
  firewall.
- The stream's encoded resolution may differ from the receiver's output size. `streamWidth` and
  `streamHeight` on the `Main Camera` component configure the RGBA texture dimensions; GStreamer
  scales decoded frames to this size. Defaults are **1280×720**.

Choose **Mono** on `ArmCameraStreamView` to show one image in both eyes, or **Side By Side** when
the incoming frame packs left/right views horizontally. The display is head-locked. Passthrough is
off by default; `PassthroughSetup.showPassthrough` can restore passthrough behind virtual content.

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
- The existing hand/pose publishing code continues to use its separate TCP connection.

The native build sources are in `Tools/gstreamer`. Rebuilding requires the official GStreamer
Android SDK **1.28.7** `arm64` directory and Unity's Android NDK. The Unity project minimum Android
API is 32; `Application.mk` uses the same API level.
