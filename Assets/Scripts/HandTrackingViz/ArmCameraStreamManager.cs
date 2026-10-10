using UnityEngine;

namespace HandTrackingViz
{
    /// <summary>
    /// Listens on the fixed arm-camera RTP port range and creates a movable window
    /// only for ports that are actively receiving decoded video.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArmCameraStreamManager : MonoBehaviour
    {
        private const int MaximumStreamCount = ArmCameraStreamView.MaximumSupportedStreams;

        [Header("Fixed incoming port pool")]
        [Min(1)] public int firstPort = 5000;
        [Range(1, MaximumStreamCount)] public int receiverCount = MaximumStreamCount;

        [Header("Video output")]
        [Min(2)] public int streamWidth = 1280;
        [Min(2)] public int streamHeight = 720;
        public ArmCameraStreamView.VideoLayout layout = ArmCameraStreamView.VideoLayout.Mono;

        [Header("Movable windows")]
        [Min(0.2f)] public float screenDistance = 1.2f;
        [Range(0.25f, 1.5f)] public float windowHeight = 0.55f;
        [Min(0.5f)] public float streamTimeoutSeconds = 2.5f;
        [SerializeField] Shader videoShader;

        private ArmCameraStreamView[] m_Receivers;
        private bool[] m_PreviousActiveStates;

        void Start()
        {
            int count = Mathf.Clamp(receiverCount, 1, MaximumStreamCount);
            if (firstPort < 1 || firstPort + count - 1 > 65535)
            {
                Debug.LogError($"[ArmCameraStreamManager] Invalid fixed port range starting at {firstPort} " +
                               $"for {count} receivers.", this);
                enabled = false;
                return;
            }

            receiverCount = count;
            m_Receivers = new ArmCameraStreamView[count];
            m_PreviousActiveStates = new bool[count];

            for (int i = 0; i < count; ++i)
            {
                int port = firstPort + i;
                var receiverObject = new GameObject($"Arm Camera RTP Receiver {port}");
                receiverObject.layer = gameObject.layer;
                receiverObject.transform.SetParent(transform, false);

                ArmCameraStreamView receiver = receiverObject.AddComponent<ArmCameraStreamView>();
                receiver.port = port;
                receiver.streamWidth = streamWidth;
                receiver.streamHeight = streamHeight;
                receiver.layout = layout;
                receiver.screenDistance = screenDistance;
                receiver.windowHeight = windowHeight;
                receiver.streamTimeoutSeconds = streamTimeoutSeconds;
                receiver.SetWindowLayout(Vector2.zero, windowHeight);
                receiver.SetVideoShader(videoShader);
                m_Receivers[i] = receiver;
            }

            Debug.Log($"[ArmCameraStreamManager] Listening on fixed RTP ports " +
                      $"{firstPort}-{firstPort + count - 1}; a window appears for each active stream.", this);
        }

        void LateUpdate()
        {
            if (m_Receivers == null)
                return;

            bool activeSetChanged = false;
            int activeCount = 0;
            for (int i = 0; i < m_Receivers.Length; ++i)
            {
                bool active = m_Receivers[i] != null && m_Receivers[i].HasVideoWindow;
                if (active != m_PreviousActiveStates[i])
                {
                    m_PreviousActiveStates[i] = active;
                    activeSetChanged = true;
                }
                if (active)
                    ++activeCount;
            }

            if (activeSetChanged)
                ApplyWindowLayout(activeCount);
        }

        void ApplyWindowLayout(int activeCount)
        {
            int activeIndex = 0;
            for (int i = 0; i < m_Receivers.Length; ++i)
            {
                ArmCameraStreamView receiver = m_Receivers[i];
                if (receiver == null || !receiver.HasVideoWindow)
                    continue;

                Vector2 offset;
                float height;
                if (activeCount <= 1)
                {
                    offset = Vector2.zero;
                    height = windowHeight;
                }
                else if (activeCount == 2)
                {
                    offset = new Vector2(activeIndex == 0 ? -0.52f : 0.52f, 0f);
                    height = Mathf.Min(windowHeight, 0.55f);
                }
                else
                {
                    // Two columns by two rows. With three active streams the last
                    // one occupies the lower-left position.
                    Vector2[] grid =
                    {
                        new Vector2(-0.32f, 0.19f),
                        new Vector2( 0.32f, 0.19f),
                        new Vector2(-0.32f,-0.19f),
                        new Vector2( 0.32f,-0.19f)
                    };
                    offset = grid[Mathf.Min(activeIndex, grid.Length - 1)];
                    height = Mathf.Min(windowHeight, 0.34f);
                }

                receiver.SetWindowLayout(offset, height);
                ++activeIndex;
            }
        }
    }
}
