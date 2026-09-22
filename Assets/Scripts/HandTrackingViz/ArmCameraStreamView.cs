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
    [DisallowMultipleComponent]
    public sealed class ArmCameraStreamView : MonoBehaviour
    {
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
        [SerializeField] Shader videoShader;

        private Texture2D m_VideoTexture;
        private Material m_VideoMaterial;
        private GameObject m_Screen;
        private AndroidJavaClass m_GStreamerClass;
        private IntPtr m_FrameBuffer;
        private int m_FrameBufferBytes;
        private bool m_GStreamerStarted;
        private bool m_StartAttempted;
        private bool m_RuntimeReady;
        private bool m_ApplicationPaused;
        private float m_NextErrorPollTime;
        private string m_LastReportedError;

#if UNITY_ANDROID && !UNITY_EDITOR
        private static class NativeGStreamerBridge
        {
            [DllImport("ArmCameraGStreamer")] internal static extern void ConfigureVideoTexture(int width, int height);
            [DllImport("ArmCameraGStreamer")] internal static extern int StartGStreamerReceiver(int port, int width, int height);
            [DllImport("ArmCameraGStreamer")] internal static extern void StopGStreamerReceiver();
            [DllImport("ArmCameraGStreamer")] internal static extern int CopyLatestFrame(IntPtr destination, int capacity);
            [DllImport("ArmCameraGStreamer")] internal static extern IntPtr GetLastGStreamerError();
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

                m_VideoTexture = new Texture2D(streamWidth, streamHeight, TextureFormat.RGBA32, false, false)
                {
                    name = "Arm camera GStreamer texture",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                m_VideoTexture.SetPixels32(new Color32[streamWidth * streamHeight]);
                m_VideoTexture.Apply(false, false);

                m_FrameBufferBytes = (int)frameBytes;
                m_FrameBuffer = Marshal.AllocHGlobal(m_FrameBufferBytes);
                if (m_FrameBuffer == IntPtr.Zero)
                    throw new OutOfMemoryException("Could not allocate the GStreamer frame staging buffer.");

                NativeGStreamerBridge.ConfigureVideoTexture(streamWidth, streamHeight);

                CreateMovableScreen(streamWidth, streamHeight);
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
            using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                m_GStreamerClass = new AndroidJavaClass("org.freedesktop.gstreamer.GStreamer");
                m_GStreamerClass.CallStatic("init", activity);
            }
        }

        void StartNativeReceiver()
        {
            if (!m_RuntimeReady || m_ApplicationPaused || m_GStreamerStarted)
                return;

            // Stop is also called after a failed start, since native startup can fail after
            // allocating a socket or creating part of the GStreamer pipeline.
            m_StartAttempted = true;
            if (NativeGStreamerBridge.StartGStreamerReceiver(port, streamWidth, streamHeight) == 0)
            {
                IntPtr errorPointer = NativeGStreamerBridge.GetLastGStreamerError();
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

            int copiedBytes = NativeGStreamerBridge.CopyLatestFrame(m_FrameBuffer, m_FrameBufferBytes);
            if (copiedBytes == m_FrameBufferBytes)
            {
                m_VideoTexture.LoadRawTextureData(m_FrameBuffer, m_FrameBufferBytes);
                m_VideoTexture.Apply(false, false);
            }
            else if (copiedBytes < 0)
            {
                // Poll immediately so a native copy error is reported without waiting
                // for the regular half-second GStreamer error interval.
                m_NextErrorPollTime = 0f;
            }

            if (Time.unscaledTime < m_NextErrorPollTime)
                return;

            m_NextErrorPollTime = Time.unscaledTime + 0.5f;
            IntPtr errorPointer = NativeGStreamerBridge.GetLastGStreamerError();
            string error = errorPointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(errorPointer);
            if (!string.IsNullOrEmpty(error) && error != m_LastReportedError)
            {
                m_LastReportedError = error;
                Debug.LogError($"[ArmCameraStreamView] GStreamer receiver error: {error}", this);
            }
        }

        void CreateMovableScreen(int width, int height)
        {
            Shader shader = videoShader != null ? videoShader : Shader.Find("Teleoperation/ArmCamera");
            if (shader == null)
                throw new InvalidOperationException("Shader Teleoperation/ArmCamera was not found.");

            m_Screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            m_Screen.name = "Arm Camera View";
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
            screenTransform.localPosition = new Vector3(0f, 0f, screenDistance);
            screenTransform.localRotation = Quaternion.identity;

            float screenHeight = Mathf.Clamp(windowHeight, 0.25f, 1.5f);
            float eyeAspect = layout == VideoLayout.SideBySide
                ? (width * 0.5f) / height
                : (float)width / height;
            screenTransform.localScale = new Vector3(screenHeight * eyeAspect, screenHeight, 1f);

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

            ArmCameraWindowInteractor interactor = m_Screen.AddComponent<ArmCameraWindowInteractor>();
            interactor.Initialize(trackingOrigin, windowCollider);
        }

        void OnDestroy()
        {
            StopReceiverAndRelease();
        }

        void StopReceiverAndRelease()
        {
            m_RuntimeReady = false;
            StopNativeReceiver();

            if (m_GStreamerClass != null)
            {
                try { m_GStreamerClass.Dispose(); } catch (Exception) { }
                m_GStreamerClass = null;
            }

            if (m_Screen != null) Destroy(m_Screen);
            if (m_VideoMaterial != null) Destroy(m_VideoMaterial);
            if (m_VideoTexture != null) Destroy(m_VideoTexture);
            if (m_FrameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(m_FrameBuffer);
                m_FrameBuffer = IntPtr.Zero;
                m_FrameBufferBytes = 0;
            }
            m_Screen = null;
            m_VideoMaterial = null;
            m_VideoTexture = null;
        }

        void StopNativeReceiver()
        {
            if (m_StartAttempted || m_GStreamerStarted)
            {
                try { NativeGStreamerBridge.StopGStreamerReceiver(); } catch (Exception) { }
            }
            m_StartAttempted = false;
            m_GStreamerStarted = false;
        }
#endif
    }
}
