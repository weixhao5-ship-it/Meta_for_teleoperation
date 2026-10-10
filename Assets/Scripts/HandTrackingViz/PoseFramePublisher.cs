using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.XR;

namespace HandTrackingViz
{
    /// <summary>
    /// The single publisher for the whole device state — HMD pose, both hands'
    /// palm + 21 MediaPipe landmarks, and both controllers' pose/inputs. It
    /// matches the wire format in SCHEMA.md exactly:
    ///
    ///   Transport : UDP — one UTF-8 JSON datagram per published packet.
    ///   Port      : 5555 (Inspector-configurable); subscribers register with a
    ///               small UDP request, then receive frames at their source endpoint.
    ///   Model     : publish-and-forget broadcast — the newest sampled state is sent
    ///               to each registered endpoint. UDP may lose or reorder packets.
    ///
    /// Rate (SCHEMA.md):
    ///   - The main thread samples poses and builds the latest JSON snapshot once
    ///     per render frame. A background thread sends the latest snapshot at
    ///     publishRateHz (60 Hz by default); packets can repeat a sample when the
    ///     render/tracking rate is below 60 Hz.
    ///
    /// Contract:
    ///   - All positions are meters. Poses are sampled in Unity world space
    ///     (left-handed, Y-up) but are converted AT SERIALIZATION TIME into the
    ///     wire frame "robot_world" — RIGHT-HANDED, +X forward, +Y left, +Z up
    ///     (FLU axis convention, ROS REP-103 style). +X forward is the XR
    ///     reference space's initial facing direction (where the user looked at
    ///     app start / recenter).
    ///       position   : (x, y, z)_unity     -> (z, -x, y)
    ///       quaternion : (x, y, z, w)_unity  -> (-z, x, -y, w)
    ///     Rotation is a quaternion in (x, y, z, w) order.
    ///   - hands is always 2 elements, order [left, right], tagged with "side".
    ///     Untracked hand -> {"side": ..., "tracked": false} (no palm/landmarks).
    ///   - controllers is likewise always 2 elements, [left, right]. Hands and
    ///     controllers are INDEPENDENT — both, either, or neither may be tracked
    ///     in a given frame; the receiver picks whichever source it wants.
    ///   - Untracked HMD -> {"tracked": false}; a landmark, palm or controller
    ///     pose with "valid": false omits its position/rotation.
    ///   - Floats are formatted "0.######" (&lt;=6 decimals, InvariantCulture).
    ///
    /// No external libraries — pure .NET sockets — so it builds cleanly for Quest
    /// (Android / IL2CPP). Requires the INTERNET permission (Player Settings ->
    /// Internet Access = Require; enabled in this project).
    /// </summary>
    public sealed class PoseFramePublisher : MonoBehaviour
    {
        public const string CoordinateSystem = "robot_world";

        [Header("Tracking")]
        [Tooltip("XR Origin transform — the same one wired into HandVisualizer. Hand joint poses are " +
                 "converted to Unity world space through it. When null, poses are treated as already in world space.")]
        public Transform trackingOrigin;

        [Tooltip("The HMD-driven camera. Its world transform is published as the HMD SE(3) pose. " +
                 "Defaults to Camera.main when left empty.")]
        public Camera hmdCamera;

        [Header("Network (UDP)")]
        [Tooltip("UDP port used for subscriber registration and pose datagrams.")]
        public int port = 5555;

        [Tooltip("Pose datagram send rate in Hz. Defaults to 100; snapshots are sampled on Unity's render frames.")]
        public int publishRateHz = 60;

        [Tooltip("Log UDP subscriber registration/expiry events to the Unity console.")]
        public bool logConnections = true;

        [Header("Palm pose")]
        public HandLandmarkProvider.PalmPoseSource palmPoseSource = HandLandmarkProvider.PalmPoseSource.OpenXRPalmJoint;

        [Header("Controllers")]
        [Tooltip("Sample and publish the hand-held controllers alongside the hands. " +
                 "Turning this off publishes both controllers as tracked:false — it does " +
                 "not remove the \"controllers\" field, so the schema stays stable.")]
        public bool publishControllers = true;

        [Header("Status HUD")]
        [Tooltip("Show four head-locked dots in the top-left of the view: HMD, left, " +
                 "right (hand or controller) and receiver registration. Green = tracked / " +
                 "connected, red = missing.")]
        public bool showStatusHud = true;

        [Header("Debug")]
        public bool logPublishCount = false;
        public float logIntervalSeconds = 5f;

        static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;
        const string NumFmt = "0.######";   // <= 6 decimals, trailing zeros trimmed

        readonly HandLandmarkProvider m_Provider = new HandLandmarkProvider();
        readonly ControllerSampler m_ControllerSampler = new ControllerSampler();
        HandLandmarkFrame m_Left;
        HandLandmarkFrame m_Right;
        bool m_LeftTracked;
        bool m_RightTracked;
        readonly ControllerData m_LeftController = new ControllerData();
        readonly ControllerData m_RightController = new ControllerData();
        StatusIndicator m_StatusHud;
        bool m_ControllerSamplingFailed;

        // Reused each frame (built on the main thread only).
        readonly StringBuilder m_Sb = new StringBuilder(4096);
        long m_PublishCounter;
        float m_LastLogTime;

        // Latest-value payload hand-off: main thread writes, send thread reads.
        readonly object m_PayloadLock = new object();
        byte[] m_LatestPayload;                // UTF-8 JSON datagram payload.

        // Registered UDP clients (publish-and-forget broadcast targets).
        readonly object m_ClientsLock = new object();
        readonly List<UdpSubscriber> m_Clients = new List<UdpSubscriber>();
        readonly List<UdpSubscriber> m_SendScratch = new List<UdpSubscriber>();

        UdpClient m_Receiver;
        Thread m_SendThread;
        volatile bool m_Running;

        const string SubscribeMessage = "META_POSE_SUBSCRIBE";
        const double SubscriberTimeoutSeconds = 3.0;

        sealed class UdpSubscriber
        {
            public readonly IPEndPoint endpoint;
            public DateTime lastSeenUtc;

            public UdpSubscriber(IPEndPoint endpoint)
            {
                this.endpoint = endpoint;
                lastSeenUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Number of currently registered UDP subscribers. Thread-safe; feeds the
        /// on-device status HUD's RECEIVER dot. Subscribers expire after their
        /// registration heartbeat stops.
        /// </summary>
        public int ConnectedClientCount
        {
            get { lock (m_ClientsLock) { return m_Clients.Count; } }
        }

        void Awake()
        {
            m_Left = new HandLandmarkFrame(HandSide.Left);
            m_Right = new HandLandmarkFrame(HandSide.Right);
            if (hmdCamera == null)
                hmdCamera = Camera.main;
        }

        void Start()
        {
            if (showStatusHud && hmdCamera != null)
                m_StatusHud = StatusIndicator.Attach(hmdCamera.transform);
        }

        void OnEnable()
        {
            try
            {
                m_Receiver = new UdpClient(port);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PoseFramePublisher] failed to open UDP port {port}: {e.Message}.");
                m_Receiver?.Close();
                m_Receiver = null;
                return;
            }

            m_Running = true;
            m_SendThread = new Thread(SendLoop) { IsBackground = true, Name = "PoseFramePublisher.Send" };
            m_SendThread.Start();

            Debug.Log($"[PoseFramePublisher] UDP pose publisher listening on 0.0.0.0:{port}; " +
                      $"sending at {Mathf.Max(1, publishRateHz)} Hz.");
        }

        void OnDisable()
        {
            m_Running = false;
            try { m_SendThread?.Join(500); } catch { /* ignore */ }
            try { m_Receiver?.Close(); } catch { /* ignore */ }

            lock (m_PayloadLock) { m_LatestPayload = null; }
            lock (m_ClientsLock) { m_Clients.Clear(); }
            m_SendThread = null;
            m_Receiver = null;
        }

        void Update()
        {
            // Sampling runs on Unity's render frame; the UDP sender publishes the
            // newest snapshot at its own configured rate.
            m_Provider.palmPoseSource = palmPoseSource;
            m_LeftTracked = m_Provider.TryUpdate(HandSide.Left, trackingOrigin, m_Left);
            m_RightTracked = m_Provider.TryUpdate(HandSide.Right, trackingOrigin, m_Right);

            if (hmdCamera == null)
                hmdCamera = Camera.main;
            PoseSE3 hmd = SampleHmdPose();

            // Controllers are sampled alongside the hands, not instead of them:
            // the runtime can report both, and the receiver picks whichever
            // source is tracked. Sampling is unconditional so a frame with no
            // controller still says so explicitly.
            //
            // Guarded because this runs before the payload hand-off: an exception
            // escaping here would abort Update() and stop fresh snapshots. Controller
            // trouble must cost the controllers, not the whole stream.
            try
            {
                if (publishControllers)
                    m_ControllerSampler.Fill(m_LeftController, m_RightController, trackingOrigin);
                else
                    ControllerSampler.MarkUntracked(m_LeftController, m_RightController);
            }
            catch (Exception e)
            {
                ControllerSampler.MarkUntracked(m_LeftController, m_RightController);
                if (!m_ControllerSamplingFailed)
                {
                    m_ControllerSamplingFailed = true;   // log once, not every frame
                    Debug.LogError($"[PoseFramePublisher] controller sampling failed, publishing them as " +
                                   $"untracked from here on. Hands/HMD are unaffected. {e}");
                }
            }

            // The HUD mirrors the latest snapshot: a dot is green only if that
            // source is tracked, and RECEIVER is green while a UDP subscriber is
            // registered.
            if (m_StatusHud != null)
                m_StatusHud.SetStates(
                    hmd.tracked,
                    m_LeftTracked || m_LeftController.tracked,
                    m_RightTracked || m_RightController.tracked,
                    ConnectedClientCount > 0);

            if (!m_Running)
                return;

            byte[] bytes = Encoding.UTF8.GetBytes(BuildJson(hmd));
            lock (m_PayloadLock) { m_LatestPayload = bytes; }

            if (logPublishCount && Time.time - m_LastLogTime >= logIntervalSeconds)
            {
                m_LastLogTime = Time.time;
                Debug.Log($"[PoseFramePublisher] {Interlocked.Read(ref m_PublishCounter)} UDP packets sent, " +
                          $"{ConnectedClientCount} client(s).");
            }
        }

        // HMD pose is read from the XR camera transform, which Unity's XR
        // subsystem updates every frame from the active OpenXR reference space.
        // Camera.transform values are already in Unity world space (left-handed,
        // Y-up, meters).
        //
        // The transform always holds SOME value — it just goes stale when the
        // runtime loses head tracking — so `tracked` is gated on the head device's
        // own isTracked flag when the runtime exposes one. Without this, the
        // wire's hmd.tracked (and the HUD's HMD dot) could never report a loss.
        PoseSE3 SampleHmdPose()
        {
            if (hmdCamera == null) return PoseSE3.NotTracked;

            bool tracked = true;
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out bool live))
                tracked = live;

            return new PoseSE3
            {
                tracked = tracked,
                position = hmdCamera.transform.position,
                rotation = hmdCamera.transform.rotation
            };
        }

        // ----------------------------------------------------------------
        // Networking threads
        // ----------------------------------------------------------------
        void ReceiveRegistrations()
        {
            // Poll the same bound socket used for sending. One worker thread owns
            // both operations, and replies originate from the registered port.
            for (int handled = 0; m_Running && handled < 8 && m_Receiver.Available > 0; handled++)
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] request;
                try { request = m_Receiver.Receive(ref remote); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException)
                {
                    return;
                }
                catch (Exception e)
                {
                    if (!m_Running) return;
                    Debug.LogError("[PoseFramePublisher] UDP receive failed: " + e.Message);
                    return;
                }

                if (Encoding.ASCII.GetString(request) != SubscribeMessage)
                    continue;

                bool added = false;
                lock (m_ClientsLock)
                {
                    UdpSubscriber subscriber = null;
                    foreach (var client in m_Clients)
                    {
                        if (client.endpoint.Equals(remote))
                        {
                            subscriber = client;
                            break;
                        }
                    }

                    if (subscriber == null)
                    {
                        subscriber = new UdpSubscriber(remote);
                        m_Clients.Add(subscriber);
                        added = true;
                    }
                    subscriber.lastSeenUtc = DateTime.UtcNow;
                }

                if (added && logConnections)
                    Debug.Log($"[PoseFramePublisher] UDP subscriber registered: {remote}.");
            }
        }

        void SendLoop()
        {
            while (m_Running)
            {
                long loopStart = System.Diagnostics.Stopwatch.GetTimestamp();
                ReceiveRegistrations();

                byte[] payload;
                lock (m_PayloadLock) { payload = m_LatestPayload; }

                m_SendScratch.Clear();
                DateTime now = DateTime.UtcNow;
                lock (m_ClientsLock)
                {
                    for (int i = m_Clients.Count - 1; i >= 0; i--)
                    {
                        if ((now - m_Clients[i].lastSeenUtc).TotalSeconds > SubscriberTimeoutSeconds)
                        {
                            if (logConnections)
                                Debug.Log($"[PoseFramePublisher] UDP subscriber expired: {m_Clients[i].endpoint}.");
                            m_Clients.RemoveAt(i);
                        }
                    }
                    m_SendScratch.AddRange(m_Clients);
                }

                if (payload != null)
                {
                    foreach (var client in m_SendScratch)
                    {
                        try
                        {
                            // Send through the bound port-5555 socket so the datagram
                            // source matches the endpoint the PC registered with.
                            m_Receiver.Send(payload, payload.Length, client.endpoint);
                            Interlocked.Increment(ref m_PublishCounter);
                        }
                        catch (Exception e)
                        {
                            if (!m_Running) break;
                            bool removed = false;
                            lock (m_ClientsLock)
                            {
                                for (int i = m_Clients.Count - 1; i >= 0; i--)
                                {
                                    if (m_Clients[i].endpoint.Equals(client.endpoint))
                                    {
                                        m_Clients.RemoveAt(i);
                                        removed = true;
                                    }
                                }
                            }
                            if (removed && logConnections)
                                Debug.LogWarning($"[PoseFramePublisher] UDP send to {client.endpoint} failed: {e.Message}");
                        }
                    }
                }

                int rateHz = Math.Max(1, publishRateHz);
                int periodMs = Math.Max(1, (int)Math.Round(1000.0 / rateHz));
                long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - loopStart;
                int elapsedMs = (int)(elapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                int waitMs = periodMs - elapsedMs;
                if (waitMs > 0)
                    Thread.Sleep(waitMs);
            }
        }

        // ----------------------------------------------------------------
        // JSON (built on the main thread, reusing `m_Sb`)
        // ----------------------------------------------------------------
        string BuildJson(PoseSE3 hmd)
        {
            var sb = m_Sb;
            sb.Clear();
            sb.Append('{');
            sb.Append("\"timestamp\":").Append(Time.timeAsDouble.ToString(NumFmt, Inv)).Append(',');
            sb.Append("\"frame\":").Append(Time.frameCount.ToString(Inv)).Append(',');
            sb.Append("\"coordinate_system\":\"").Append(CoordinateSystem).Append("\",");

            // hmd: { "tracked": bool, ["position", "rotation"] }
            sb.Append("\"hmd\":");
            AppendHmd(sb, hmd);
            sb.Append(',');

            // hands: exactly 2, order [left, right], each tagged with "side".
            sb.Append("\"hands\":[");
            AppendHand(sb, "left", m_Left, m_LeftTracked);
            sb.Append(',');
            AppendHand(sb, "right", m_Right, m_RightTracked);
            sb.Append("],");

            // controllers: exactly 2, order [left, right], same shape contract as
            // hands. Additive — a consumer that only reads "hands" is unaffected.
            sb.Append("\"controllers\":[");
            AppendController(sb, "left", m_LeftController);
            sb.Append(',');
            AppendController(sb, "right", m_RightController);
            sb.Append(']');

            sb.Append('}');
            sb.Append('\n');   // Whitespace terminator; one complete JSON object is sent per datagram.
            return sb.ToString();
        }

        static void AppendHmd(StringBuilder sb, PoseSE3 hmd)
        {
            sb.Append("{\"tracked\":").Append(hmd.tracked ? "true" : "false");
            if (hmd.tracked)
            {
                sb.Append(",\"position\":"); AppendVec3(sb, hmd.position);
                sb.Append(",\"rotation\":"); AppendQuat(sb, hmd.rotation);
            }
            sb.Append('}');
        }

        static void AppendHand(StringBuilder sb, string side, HandLandmarkFrame frame, bool tracked)
        {
            sb.Append("{\"side\":\"").Append(side).Append("\",\"tracked\":")
              .Append(tracked ? "true" : "false");
            if (!tracked) { sb.Append('}'); return; }

            // palm: { "valid": bool, ["position", "rotation"] }
            sb.Append(",\"palm\":{\"valid\":").Append(frame.palmValid ? "true" : "false");
            if (frame.palmValid)
            {
                sb.Append(",\"position\":"); AppendVec3(sb, frame.palmPose.position);
                sb.Append(",\"rotation\":"); AppendQuat(sb, frame.palmPose.rotation);
            }
            sb.Append('}');

            // landmarks: exactly 21, objects { "i", "valid", ["position", "rotation"] }
            sb.Append(",\"landmarks\":[");
            for (int i = 0; i < HandLandmarkMapping.LandmarkCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"i\":").Append(i.ToString(Inv))
                  .Append(",\"valid\":").Append(frame.valid[i] ? "true" : "false");
                if (frame.valid[i])
                {
                    sb.Append(",\"position\":"); AppendVec3(sb, frame.positions[i]);
                    sb.Append(",\"rotation\":"); AppendQuat(sb, frame.rotations[i]);
                }
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
        }

        // One controller: the handle pose + analog/digital inputs. Mirrors
        // AppendHand's contract — an untracked controller is {"side",
        // "tracked":false} with nothing else, and an invalid pose omits
        // position/rotation.
        //
        // Wire names are teleop-facing (see ControllerData in PoseSnapshot.cs):
        // "controller" is the handle POSE, "grip_trigger" is the INDEX trigger
        // (drives the gripper downstream) and "ctrl_trigger" is the hardware
        // grip/squeeze axis. See the controllers section in SCHEMA.md.
        static void AppendController(StringBuilder sb, string side, ControllerData c)
        {
            sb.Append("{\"side\":\"").Append(side).Append("\",\"tracked\":")
              .Append(c.tracked ? "true" : "false");
            if (!c.tracked) { sb.Append('}'); return; }

            sb.Append(",\"controller\":"); AppendValidPose(sb, c.pose);

            sb.Append(",\"grip_trigger\":").Append(c.gripTrigger.ToString(NumFmt, Inv));
            sb.Append(",\"ctrl_trigger\":").Append(c.ctrlTrigger.ToString(NumFmt, Inv));
            sb.Append(",\"thumbstick\":"); AppendVec2(sb, c.thumbstick);

            sb.Append(",\"buttons\":{\"primary\":").Append(c.primaryButton ? "true" : "false")
              .Append(",\"secondary\":").Append(c.secondaryButton ? "true" : "false")
              .Append(",\"menu\":").Append(c.menuButton ? "true" : "false")
              .Append(",\"thumbstick\":").Append(c.thumbstickClicked ? "true" : "false")
              .Append('}');

            sb.Append('}');
        }

        // A pose carried as { "valid": bool, ["position", "rotation"] } — the
        // same shape the palm and each landmark use.
        static void AppendValidPose(StringBuilder sb, PoseSE3 p)
        {
            sb.Append("{\"valid\":").Append(p.tracked ? "true" : "false");
            if (p.tracked)
            {
                sb.Append(",\"position\":"); AppendVec3(sb, p.position);
                sb.Append(",\"rotation\":"); AppendQuat(sb, p.rotation);
            }
            sb.Append('}');
        }

        // World rotation as a [x, y, z, w] array in the robot_world wire frame.
        // Basis change from Unity (left-handed, Y-up) to FLU (right-handed,
        // +X forward, +Y left, +Z up): (x, y, z, w) -> (-z, x, -y, w).
        static void AppendQuat(StringBuilder sb, Quaternion q)
        {
            sb.Append('[');
            sb.Append((-q.z).ToString(NumFmt, Inv)).Append(',');
            sb.Append(q.x.ToString(NumFmt, Inv)).Append(',');
            sb.Append((-q.y).ToString(NumFmt, Inv)).Append(',');
            sb.Append(q.w.ToString(NumFmt, Inv));
            sb.Append(']');
        }

        // World position as a [x, y, z] array (meters) in the robot_world wire
        // frame. Same basis change as AppendQuat: (x, y, z) -> (z, -x, y).
        static void AppendVec3(StringBuilder sb, Vector3 v)
        {
            sb.Append('[');
            sb.Append(v.z.ToString(NumFmt, Inv)).Append(',');
            sb.Append((-v.x).ToString(NumFmt, Inv)).Append(',');
            sb.Append(v.y.ToString(NumFmt, Inv));
            sb.Append(']');
        }

        // Vector2 as an [x, y] array (unitless axis, each -1..1).
        static void AppendVec2(StringBuilder sb, Vector2 v)
        {
            sb.Append('[');
            sb.Append(v.x.ToString(NumFmt, Inv)).Append(',');
            sb.Append(v.y.ToString(NumFmt, Inv));
            sb.Append(']');
        }
    }
}
