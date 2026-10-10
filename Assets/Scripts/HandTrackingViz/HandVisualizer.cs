using UnityEngine;

namespace HandTrackingViz
{
    /// <summary>
    /// Scene-facing component that drives hand-landmark and controller visualization on top of passthrough.
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

        [Header("Markers (landmarks)")]
        [Tooltip("Colour of the LEFT hand's joint spheres. Kept identical to the " +
                 "Galaxy_with_hands edition so the two look the same.")]
        public Color leftHandColor = Color.red;
        [Tooltip("Colour of the RIGHT hand's joint spheres. Deliberately different from " +
                 "the left one so the hands stay distinguishable.")]
        public Color rightHandColor = new Color(1f, 0.4f, 0.1f); // orange-red

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

        [Header("Controllers")]
        [Tooltip("Show the tracked left and right controller models alongside hand tracking.")]
        public bool showControllers = true;
        [Tooltip("Optional left controller model prefab. A simple generic model is used when empty.")]
        public GameObject leftControllerModelPrefab;
        [Tooltip("Optional right controller model prefab. A simple generic model is used when empty.")]
        public GameObject rightControllerModelPrefab;
        readonly HandLandmarkProvider m_Provider = new HandLandmarkProvider();
        readonly ControllerSampler m_ControllerSampler = new ControllerSampler();

        HandLandmarkFrame m_LeftFrame;
        HandLandmarkFrame m_RightFrame;
        readonly ControllerData m_LeftController = new ControllerData();
        readonly ControllerData m_RightController = new ControllerData();
        HandVisual m_LeftVisual;
        HandVisual m_RightVisual;
        ControllerVisual m_LeftControllerVisual;
        ControllerVisual m_RightControllerVisual;
        bool m_ControllerSamplingFailed;

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

            m_LeftVisual = CreateVisual("LeftHandVisual", m_LeftMat);
            m_RightVisual = CreateVisual("RightHandVisual", m_RightMat);
            m_LeftControllerVisual = new ControllerVisual(
                transform, "LeftControllerVisual", leftControllerModelPrefab, m_LeftMat);
            m_RightControllerVisual = new ControllerVisual(
                transform, "RightControllerVisual", rightControllerModelPrefab, m_RightMat);
        }

        HandVisual CreateVisual(string name, Material handMat)
        {
            return new HandVisual(
                transform,
                name,
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

            // Keep controller visualization independent from hand tracking. The XR
            // runtime can report either source, and sometimes both at once.
            try
            {
                m_ControllerSampler.Fill(m_LeftController, m_RightController, trackingOrigin);
            }
            catch (System.Exception e)
            {
                ControllerSampler.MarkUntracked(m_LeftController, m_RightController);
                if (!m_ControllerSamplingFailed)
                {
                    m_ControllerSamplingFailed = true;
                    Debug.LogError("[HandVisualizer] controller sampling failed; controller visuals " +
                                   "will remain hidden. " + e);
                }
            }

            UpdateController(m_LeftController, m_LeftControllerVisual);
            UpdateController(m_RightController, m_RightControllerVisual);
        }

        void UpdateController(ControllerData controller, ControllerVisual visual)
        {
            if (!showControllers || !controller.tracked)
            {
                visual.SetVisible(false);
                return;
            }

            visual.SetPose(controller.pose);
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

        void OnDisable()
        {
            if (m_LeftControllerVisual != null) m_LeftControllerVisual.SetVisible(false);
            if (m_RightControllerVisual != null) m_RightControllerVisual.SetVisible(false);
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

        /// <summary>
        /// Places a controller model at the OpenXR grip pose. The prefab keeps its
        /// own local offset so standard XRI controller models line up with that pose.
        /// </summary>
        sealed class ControllerVisual
        {
            const float kUpwardPitchOffsetDegrees = 45f;
            readonly GameObject m_RootObject;
            readonly Transform m_Root;
            readonly Transform m_ModelRoot;

            public ControllerVisual(Transform parent, string name, GameObject modelPrefab,
                Material fallbackMaterial)
            {
                m_RootObject = new GameObject(name);
                m_RootObject.SetActive(false);
                m_Root = m_RootObject.transform;
                m_Root.SetParent(parent, false);

                var modelRootObject = new GameObject("ControllerModelPitchOffset");
                m_ModelRoot = modelRootObject.transform;
                m_ModelRoot.SetParent(m_Root, false);
                // This model prefab's local axis convention makes positive X raise its head.
                m_ModelRoot.localRotation = Quaternion.Euler(kUpwardPitchOffsetDegrees, 0f, 0f);

                if (modelPrefab != null)
                {
                    var modelInstance = Object.Instantiate(modelPrefab, m_ModelRoot, false);
                    // The XRI prefab root carries a 5 cm grip offset. Use that offset
                    // as the pivot so pitch correction does not shift the model in space.
                    m_ModelRoot.localPosition = modelInstance.transform.localPosition;
                    modelInstance.transform.localPosition = Vector3.zero;
                }
                else
                {
                    m_ModelRoot.localPosition = Vector3.zero;
                    CreateFallbackModel(fallbackMaterial);
                }

                SetVisible(false);
            }

            public void SetPose(PoseSE3 pose)
            {
                m_Root.SetPositionAndRotation(pose.position, pose.rotation);
                SetVisible(true);
            }

            public void SetVisible(bool visible)
            {
                if (m_RootObject.activeSelf != visible)
                    m_RootObject.SetActive(visible);
            }

            void CreateFallbackModel(Material material)
            {
                // A small generic grip and controller head keeps the visual useful
                // even in scenes that do not assign the sample XRI model prefabs.
                var grip = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                grip.name = "GenericControllerGrip";
                Object.Destroy(grip.GetComponent<Collider>());
                grip.transform.SetParent(m_ModelRoot, false);
                grip.transform.localPosition = new Vector3(0f, -0.035f, 0f);
                grip.transform.localScale = new Vector3(0.022f, 0.045f, 0.022f);
                grip.GetComponent<Renderer>().sharedMaterial = material;

                var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
                head.name = "GenericControllerHead";
                Object.Destroy(head.GetComponent<Collider>());
                head.transform.SetParent(m_ModelRoot, false);
                head.transform.localPosition = new Vector3(0f, 0.012f, 0.018f);
                head.transform.localScale = new Vector3(0.07f, 0.025f, 0.055f);
                head.GetComponent<Renderer>().sharedMaterial = material;
            }
        }
    }
}
