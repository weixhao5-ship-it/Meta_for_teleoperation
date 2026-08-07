using UnityEngine;

namespace HandTrackingViz
{
    /// <summary>
    /// Scene-facing component that drives hand-landmark visualization on top of passthrough.
    ///
    /// It wires together the reusable calculation layer (<see cref="HandLandmarkProvider"/>) and the
    /// rendering layer (<see cref="HandVisual"/>). The visualization is rendered in unity_world
    /// (left-handed, Y-up) — the same in-memory poses <see cref="PoseFramePublisher"/> publishes
    /// (the publisher re-expresses them in the right-handed "robot_world" frame at serialization,
    /// see SCHEMA.md).
    ///
    /// Add this to any GameObject in an XR scene and assign <see cref="trackingOrigin"/> to the XR Origin.
    /// </summary>
    public sealed class HandVisualizer : MonoBehaviour
    {
        [Header("Tracking")]
        [Tooltip("XR Origin transform. XR Hands joint poses are relative to this and are converted to unity_world. " +
                 "Leave empty only if your rig places the origin at world identity.")]
        public Transform trackingOrigin;

        [Header("Hand colors")]
        public Color rightHandColor = Color.cyan;
        public Color leftHandColor = Color.magenta;

        [Header("Bones (skeleton lines)")]
        [Tooltip("Colour of the lines connecting the landmarks. Shared by both hands and " +
                 "kept identical to the Galaxy_with_hands edition so the two look the same.")]
        public Color boneColor = Color.green;

        [Header("Sizes (meters)")]
        [Tooltip("Radius of each joint sphere.")]
        public float jointSphereRadius = 0.008f;
        [Tooltip("Width of the bone lines.")]
        public float boneLineWidth = 0.003f;
        [Tooltip("Length of the palm coordinate-frame axes (default 5 cm).")]
        public float axisLength = 0.05f;
        [Tooltip("Width of the palm coordinate-frame axis lines.")]
        public float axisLineWidth = 0.004f;

        [Header("Palm pose")]
        public HandLandmarkProvider.PalmPoseSource palmPoseSource = HandLandmarkProvider.PalmPoseSource.OpenXRPalmJoint;

        readonly HandLandmarkProvider m_Provider = new HandLandmarkProvider();

        HandLandmarkFrame m_LeftFrame;
        HandLandmarkFrame m_RightFrame;
        HandVisual m_LeftVisual;
        HandVisual m_RightVisual;

        // Procedurally created materials (unlit, so passthrough lighting doesn't affect them).
        Material m_LeftMat;
        Material m_RightMat;
        Material m_BoneMat;
        Material m_AxisXMat;
        Material m_AxisYMat;
        Material m_AxisZMat;

        void Awake()
        {
            m_LeftFrame = new HandLandmarkFrame(HandSide.Left);
            m_RightFrame = new HandLandmarkFrame(HandSide.Right);

            m_LeftMat = MakeUnlitMaterial(leftHandColor);
            m_RightMat = MakeUnlitMaterial(rightHandColor);
            m_BoneMat = MakeUnlitMaterial(boneColor);    // shared by both hands
            m_AxisXMat = MakeUnlitMaterial(Color.red);   // X
            m_AxisYMat = MakeUnlitMaterial(Color.green); // Y
            m_AxisZMat = MakeUnlitMaterial(Color.blue);  // Z

            m_LeftVisual = CreateVisual("LeftHandVisual", leftHandColor, m_LeftMat);
            m_RightVisual = CreateVisual("RightHandVisual", rightHandColor, m_RightMat);
        }

        HandVisual CreateVisual(string name, Color handColor, Material handMat)
        {
            return new HandVisual(
                transform,
                name,
                handColor,
                jointSphereRadius,
                boneLineWidth,
                axisLength,
                axisLineWidth,
                handMat,
                m_BoneMat,
                m_AxisXMat,
                m_AxisYMat,
                m_AxisZMat);
        }

        void Update()
        {
            m_Provider.palmPoseSource = palmPoseSource;
            UpdateHand(HandSide.Left, m_LeftFrame, m_LeftVisual);
            UpdateHand(HandSide.Right, m_RightFrame, m_RightVisual);
        }

        void UpdateHand(HandSide side, HandLandmarkFrame frame, HandVisual visual)
        {
            bool tracked = m_Provider.TryUpdate(side, trackingOrigin, frame);
            if (!tracked)
            {
                // Tracking loss (or subsystem not running): skip rendering this hand.
                visual.SetVisible(false);
                return;
            }

            visual.SetVisible(true);
            visual.Render(frame);
        }

        void OnDestroy()
        {
            DestroyMaterial(m_LeftMat);
            DestroyMaterial(m_RightMat);
            DestroyMaterial(m_BoneMat);
            DestroyMaterial(m_AxisXMat);
            DestroyMaterial(m_AxisYMat);
            DestroyMaterial(m_AxisZMat);
        }

        static void DestroyMaterial(Material m)
        {
            if (m != null)
                Destroy(m);
        }

        /// <summary>Creates a simple unlit, solid-color material that works under URP (with fallbacks).</summary>
        static Material MakeUnlitMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            mat.color = color;
            return mat;
        }
    }
}
