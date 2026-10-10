using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace HandTrackingViz
{
    /// <summary>
    /// Receives RTP/H.264 with GStreamer on Quest, requires Android MediaCodec hardware
    /// decoding, and shows the latest decoded frame on a movable Unity screen.
    /// </summary>
    public sealed class ArmCameraStreamView : MonoBehaviour
    {
        public const int MaximumSupportedStreams = 4;

        public enum VideoLayout
        {
            Mono = 0,
            SideBySide = 1
        }

        [Header("Incoming RTP/H.264 stream")]
        [Min(1)] public int port = 5000;
        [Min(2)] public int streamWidth = 1280;
        [Min(2)] public int streamHeight = 720;
        public VideoLayout layout = VideoLayout.Mono;

        [Header("Movable spatial display")]
        [Min(0.2f)] public float screenDistance = 1.2f;
        [Range(0.25f, 1.5f)] public float windowHeight = 0.55f;
        [Min(0.5f)] public float streamTimeoutSeconds = 2.5f;
        [Tooltip("Initial horizontal offset and vertical height from the HMD's level heading, in meters.")]
        public Vector2 initialWindowOffset = Vector2.zero;
        [SerializeField] Shader videoShader;

        private Texture2D m_VideoTexture;
        private Material m_VideoMaterial;
        private GameObject m_Screen;
        private IntPtr m_FrameBuffer;
        private int m_FrameBufferBytes;
#if UNITY_ANDROID && !UNITY_EDITOR
        private int m_NativeReceiverSlot = -1;
#endif
        private bool m_GStreamerStarted;
        private bool m_StartAttempted;
        private bool m_RuntimeReady;
        private bool m_ApplicationPaused;
        private float m_NextErrorPollTime;
#if UNITY_ANDROID && !UNITY_EDITOR
        private float m_LastFrameTime = -1f;
#endif
        private string m_LastReportedError;
        private bool m_WindowCreationFailed;

        public bool HasVideoWindow => m_Screen != null;

        public void SetWindowLayout(Vector2 offset, float height)
        {
            initialWindowOffset = offset;
            windowHeight = Mathf.Clamp(height, 0.25f, 1.5f);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Screen != null)
                ApplyWindowLayout();
#endif
        }

        public void SetVideoShader(Shader shader)
        {
            if (shader != null)
                videoShader = shader;
        }

        private static bool s_GStreamerInitialized;
        private static readonly bool[] s_NativeReceiverSlots = new bool[MaximumSupportedStreams];
        private static readonly object s_NativeReceiverSlotsLock = new object();

#if UNITY_ANDROID && !UNITY_EDITOR
        private static class NativeGStreamerBridge
        {
            private static class Slot0
            {
                [DllImport("ArmCameraGStreamer")] internal static extern void ConfigureVideoTexture(int width, int height);
                [DllImport("ArmCameraGStreamer")] internal static extern int StartGStreamerReceiver(int port, int width, int height);
                [DllImport("ArmCameraGStreamer")] internal static extern void StopGStreamerReceiver();
                [DllImport("ArmCameraGStreamer")] internal static extern int CopyLatestFrame(IntPtr destination, int capacity);
                [DllImport("ArmCameraGStreamer")] internal static extern IntPtr GetLastGStreamerError();
            }

            private static class Slot1
            {
                [DllImport("ArmCameraGStream01")] internal static extern void ConfigureVideoTexture(int width, int height);
                [DllImport("ArmCameraGStream01")] internal static extern int StartGStreamerReceiver(int port, int width, int height);
                [DllImport("ArmCameraGStream01")] internal static extern void StopGStreamerReceiver();
                [DllImport("ArmCameraGStream01")] internal static extern int CopyLatestFrame(IntPtr destination, int capacity);
                [DllImport("ArmCameraGStream01")] internal static extern IntPtr GetLastGStreamerError();
            }

            private static class Slot2
            {
                [DllImport("ArmCameraGStream02")] internal static extern void ConfigureVideoTexture(int width, int height);
                [DllImport("ArmCameraGStream02")] internal static extern int StartGStreamerReceiver(int port, int width, int height);
                [DllImport("ArmCameraGStream02")] internal static extern void StopGStreamerReceiver();
                [DllImport("ArmCameraGStream02")] internal static extern int CopyLatestFrame(IntPtr destination, int capacity);
                [DllImport("ArmCameraGStream02")] internal static extern IntPtr GetLastGStreamerError();
            }

            private static class Slot3
            {
                [DllImport("ArmCameraGStream03")] internal static extern void ConfigureVideoTexture(int width, int height);
                [DllImport("ArmCameraGStream03")] internal static extern int StartGStreamerReceiver(int port, int width, int height);
                [DllImport("ArmCameraGStream03")] internal static extern void StopGStreamerReceiver();
                [DllImport("ArmCameraGStream03")] internal static extern int CopyLatestFrame(IntPtr destination, int capacity);
                [DllImport("ArmCameraGStream03")] internal static extern IntPtr GetLastGStreamerError();
            }

            internal static void Configure(int slot, int width, int height)
            {
                switch (slot)
                {
                    case 0: Slot0.ConfigureVideoTexture(width, height); break;
                    case 1: Slot1.ConfigureVideoTexture(width, height); break;
                    case 2: Slot2.ConfigureVideoTexture(width, height); break;
                    case 3: Slot3.ConfigureVideoTexture(width, height); break;
                    default: throw new ArgumentOutOfRangeException(nameof(slot));
                }
            }

            internal static int Start(int slot, int port, int width, int height)
            {
                switch (slot)
                {
                    case 0: return Slot0.StartGStreamerReceiver(port, width, height);
                    case 1: return Slot1.StartGStreamerReceiver(port, width, height);
                    case 2: return Slot2.StartGStreamerReceiver(port, width, height);
                    case 3: return Slot3.StartGStreamerReceiver(port, width, height);
                    default: throw new ArgumentOutOfRangeException(nameof(slot));
                }
            }

            internal static void Stop(int slot)
            {
                switch (slot)
                {
                    case 0: Slot0.StopGStreamerReceiver(); break;
                    case 1: Slot1.StopGStreamerReceiver(); break;
                    case 2: Slot2.StopGStreamerReceiver(); break;
                    case 3: Slot3.StopGStreamerReceiver(); break;
                }
            }

            internal static int Copy(int slot, IntPtr destination, int capacity)
            {
                switch (slot)
                {
                    case 0: return Slot0.CopyLatestFrame(destination, capacity);
                    case 1: return Slot1.CopyLatestFrame(destination, capacity);
                    case 2: return Slot2.CopyLatestFrame(destination, capacity);
                    case 3: return Slot3.CopyLatestFrame(destination, capacity);
                    default: return -1;
                }
            }

            internal static IntPtr Error(int slot)
            {
                switch (slot)
                {
                    case 0: return Slot0.GetLastGStreamerError();
                    case 1: return Slot1.GetLastGStreamerError();
                    case 2: return Slot2.GetLastGStreamerError();
                    case 3: return Slot3.GetLastGStreamerError();
                    default: return IntPtr.Zero;
                }
            }
        }
#endif

        void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            StartCoroutine(InitializeAndroidReceiver());
#else
            Debug.Log("[ArmCameraStreamView] GStreamer RTP display is available in an Android Quest build.", this);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        IEnumerator InitializeAndroidReceiver()
        {
            long frameBytes = (long)streamWidth * streamHeight * 4;
            if (port < 1 || port > 65535 || streamWidth < 2 || streamHeight < 2 ||
                frameBytes > 128L * 1024 * 1024)
            {
                Debug.LogError("[ArmCameraStreamView] Invalid UDP port or output dimensions; " +
                               "RGBA frame size must not exceed 128 MiB.", this);
                yield break;
            }

            // Wait briefly for OpenXR to supply the first tracked HMD pose, otherwise a
            // world-anchored window created during Start can appear away from the user.
            float trackingDeadline = Time.realtimeSinceStartup + 2f;
            InputDevice headset = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            while (Time.realtimeSinceStartup < trackingDeadline)
            {
                if (!headset.isValid)
                    headset = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                if (headset.isValid &&
                    headset.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) &&
                    isTracked)
                    break;
                yield return null;
            }
            yield return new WaitForEndOfFrame();

            Exception initializationError = null;
            try
            {
                InitializeGStreamer();

                m_FrameBufferBytes = (int)frameBytes;
                m_FrameBuffer = Marshal.AllocHGlobal(m_FrameBufferBytes);
                if (m_FrameBuffer == IntPtr.Zero)
                    throw new OutOfMemoryException("Could not allocate the GStreamer frame staging buffer.");

                m_NativeReceiverSlot = AcquireNativeReceiverSlot();
                NativeGStreamerBridge.Configure(m_NativeReceiverSlot, streamWidth, streamHeight);

                m_RuntimeReady = true;
                StartNativeReceiver();
            }
            catch (Exception exception)
            {
                initializationError = exception;
            }

            if (initializationError != null)
            {
                Debug.LogError($"[ArmCameraStreamView] GStreamer initialization failed: {initializationError}", this);
                StopReceiverAndRelease();
            }
        }

        void InitializeGStreamer()
        {
            if (s_GStreamerInitialized)
                return;

            using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (AndroidJavaClass gstreamer = new AndroidJavaClass("org.freedesktop.gstreamer.GStreamer"))
            {
                gstreamer.CallStatic("init", activity);
            }
            s_GStreamerInitialized = true;
        }

        void StartNativeReceiver()
        {
            if (!m_RuntimeReady || m_ApplicationPaused || m_GStreamerStarted)
                return;

            m_StartAttempted = true;
            if (NativeGStreamerBridge.Start(
                    m_NativeReceiverSlot, port, streamWidth, streamHeight) == 0)
            {
                IntPtr errorPointer = NativeGStreamerBridge.Error(m_NativeReceiverSlot);
                string error = errorPointer == IntPtr.Zero
                    ? "GStreamer could not start an Android MediaCodec hardware decoder."
                    : Marshal.PtrToStringAnsi(errorPointer);
                StopNativeReceiver();
                throw new InvalidOperationException(error);
            }

            m_GStreamerStarted = true;
            m_LastReportedError = null;
            m_NextErrorPollTime = Time.unscaledTime;
            Debug.Log($"[ArmCameraStreamView] GStreamer listening on UDP :{port}; " +
                      $"Android MediaCodec hardware decoder required; output {streamWidth}x{streamHeight}.", this);
        }

        void OnApplicationPause(bool paused)
        {
            m_ApplicationPaused = paused;
            if (!m_RuntimeReady)
                return;

            if (paused)
            {
                StopNativeReceiver();
                return;
            }

            try
            {
                StartNativeReceiver();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[ArmCameraStreamView] GStreamer resume failed: {exception}", this);
            }
        }

        void LateUpdate()
        {
            if (!m_GStreamerStarted)
                return;

            int copiedBytes = NativeGStreamerBridge.Copy(
                m_NativeReceiverSlot, m_FrameBuffer, m_FrameBufferBytes);
            if (copiedBytes == m_FrameBufferBytes)
            {
                m_LastFrameTime = Time.unscaledTime;
                if (!m_WindowCreationFailed)
                {
                    try
                    {
                        if (m_VideoTexture == null)
                            CreateVideoTexture();
                        m_VideoTexture.LoadRawTextureData(m_FrameBuffer, m_FrameBufferBytes);
                        m_VideoTexture.Apply(false, false);
                        if (m_Screen == null)
                            CreateMovableScreen(streamWidth, streamHeight);
                    }
                    catch (Exception exception)
                    {
                        m_WindowCreationFailed = true;
                        Debug.LogError($"[ArmCameraStreamView] Could not create the video window: {exception}", this);
                    }
                }
            }
            else if (copiedBytes < 0)
            {
                // Poll immediately so a native copy error is reported without waiting
                // for the regular half-second GStreamer error interval.
                m_NextErrorPollTime = 0f;
            }

            if (m_Screen != null && m_LastFrameTime >= 0f &&
                Time.unscaledTime - m_LastFrameTime >= Mathf.Max(0.5f, streamTimeoutSeconds))
            {
                Debug.Log($"[ArmCameraStreamView] No decoded frames on UDP :{port} for " +
                          $"{streamTimeoutSeconds:0.0}s; closing its window.", this);
                DestroyVideoWindowResources();
            }

            if (Time.unscaledTime < m_NextErrorPollTime)
                return;

            m_NextErrorPollTime = Time.unscaledTime + 0.5f;
            IntPtr errorPointer = NativeGStreamerBridge.Error(m_NativeReceiverSlot);
            string error = errorPointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(errorPointer);
            if (!string.IsNullOrEmpty(error) && error != m_LastReportedError)
            {
                m_LastReportedError = error;
                Debug.LogError($"[ArmCameraStreamView] GStreamer receiver error: {error}", this);
            }
        }

        void CreateVideoTexture()
        {
            m_VideoTexture = new Texture2D(streamWidth, streamHeight, TextureFormat.RGBA32, false, false)
            {
                name = $"Arm camera GStreamer texture UDP {port}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        void CreateMovableScreen(int width, int height)
        {
            Shader shader = videoShader != null ? videoShader : Shader.Find("Teleoperation/ArmCamera");
            if (shader == null)
                throw new InvalidOperationException("Shader Teleoperation/ArmCamera was not found.");

            m_Screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            m_Screen.name = $"Arm Camera View UDP {port}";
            m_Screen.layer = gameObject.layer;
            Collider screenCollider = m_Screen.GetComponent<Collider>();
            if (screenCollider != null)
            {
                screenCollider.enabled = false;
                Destroy(screenCollider);
            }
            BoxCollider windowCollider = m_Screen.AddComponent<BoxCollider>();
            windowCollider.size = new Vector3(1f, 1f, 0.02f);

            Transform screenTransform = m_Screen.transform;
            screenTransform.SetParent(transform, false);

            Renderer screenRenderer = m_Screen.GetComponent<Renderer>();
            screenRenderer.shadowCastingMode = ShadowCastingMode.Off;
            screenRenderer.receiveShadows = false;
            m_VideoMaterial = new Material(shader) { name = "Arm camera GStreamer material" };
            m_VideoMaterial.mainTexture = m_VideoTexture;
            m_VideoMaterial.SetFloat("_StereoLayout", layout == VideoLayout.SideBySide ? 1f : 0f);
            screenRenderer.sharedMaterial = m_VideoMaterial;

            // Keep the panel in XR tracking space after its initial placement in front of the HMD.
            // This makes it behave like a spatial window instead of following every head movement.
            Transform trackingOrigin = transform.root;
            screenTransform.SetParent(trackingOrigin, true);
            ApplyWindowLayout();

            ArmCameraWindowInteractor interactor = m_Screen.AddComponent<ArmCameraWindowInteractor>();
            interactor.Initialize(trackingOrigin, windowCollider);
        }

        void ApplyWindowLayout()
        {
            if (m_Screen == null)
                return;

            Transform screenTransform = m_Screen.transform;
            // Make the screen's local X axis explicitly horizontal. Projecting the
            // HMD's right vector onto the ground plane removes head pitch/roll, then
            // rebuild an orthogonal upright basis from that level horizontal axis.
            Vector3 horizontalRight = Vector3.ProjectOnPlane(transform.right, Vector3.up);
            if (horizontalRight.sqrMagnitude < 0.0001f)
                horizontalRight = Vector3.ProjectOnPlane(transform.root.right, Vector3.up);
            if (horizontalRight.sqrMagnitude < 0.0001f)
                horizontalRight = Vector3.right;
            horizontalRight.Normalize();
            Vector3 horizontalForward = Vector3.Cross(horizontalRight, Vector3.up).normalized;

            screenTransform.position = transform.position
                                     + horizontalForward * screenDistance
                                     + horizontalRight * initialWindowOffset.x
                                     + Vector3.up * initialWindowOffset.y;
            screenTransform.rotation = Quaternion.LookRotation(horizontalForward, Vector3.up);

            float screenHeight = Mathf.Clamp(windowHeight, 0.25f, 1.5f);
            float eyeAspect = layout == VideoLayout.SideBySide
                ? (streamWidth * 0.5f) / streamHeight
                : (float)streamWidth / streamHeight;
            screenTransform.localScale = new Vector3(screenHeight * eyeAspect, screenHeight, 1f);
        }

        void OnDestroy()
        {
            StopReceiverAndRelease();
        }

        void StopReceiverAndRelease()
        {
            m_RuntimeReady = false;
            StopNativeReceiver();

            DestroyVideoWindowResources();
            if (m_FrameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(m_FrameBuffer);
                m_FrameBuffer = IntPtr.Zero;
                m_FrameBufferBytes = 0;
            }

            ReleaseNativeReceiverSlot();
        }

        void DestroyVideoWindowResources()
        {
            if (m_Screen != null) Destroy(m_Screen);
            if (m_VideoMaterial != null) Destroy(m_VideoMaterial);
            if (m_VideoTexture != null) Destroy(m_VideoTexture);
            m_Screen = null;
            m_VideoMaterial = null;
            m_VideoTexture = null;
            m_LastFrameTime = -1f;
            m_WindowCreationFailed = false;
        }

        void StopNativeReceiver()
        {
            if (m_NativeReceiverSlot >= 0 && (m_StartAttempted || m_GStreamerStarted))
            {
                try { NativeGStreamerBridge.Stop(m_NativeReceiverSlot); }
                catch (Exception) { }
            }
            m_StartAttempted = false;
            m_GStreamerStarted = false;
        }

        static int AcquireNativeReceiverSlot()
        {
            lock (s_NativeReceiverSlotsLock)
            {
                for (int slot = 0; slot < s_NativeReceiverSlots.Length; ++slot)
                {
                    if (s_NativeReceiverSlots[slot])
                        continue;
                    s_NativeReceiverSlots[slot] = true;
                    return slot;
                }
            }
            throw new InvalidOperationException("At most four simultaneous GStreamer video streams are supported.");
        }

        void ReleaseNativeReceiverSlot()
        {
            if (m_NativeReceiverSlot < 0)
                return;
            lock (s_NativeReceiverSlotsLock)
                s_NativeReceiverSlots[m_NativeReceiverSlot] = false;
            m_NativeReceiverSlot = -1;
        }
#endif
    }
}
