using UnityEngine;

namespace HandTrackingViz
{
    /// <summary>
    /// Owns and updates the runtime GameObjects used to draw a single hand: joint spheres,
    /// bone lines and the palm coordinate frame. Pure rendering — it consumes a
    /// <see cref="HandLandmarkFrame"/> produced by <see cref="HandLandmarkProvider"/> and never
    /// reads OpenXR directly. All visuals are created procedurally, so no scene wiring is required.
    /// </summary>
    public sealed class HandVisual
    {
        readonly Transform m_Root;
        readonly Transform[] m_Joints;
        readonly LineRenderer[] m_Bones;
        readonly LineRenderer m_AxisX;
        readonly LineRenderer m_AxisY;
        readonly LineRenderer m_AxisZ;

        readonly float m_AxisLength;

        public HandVisual(
            Transform parent,
            string name,
            Color handColor,
            float jointSphereRadius,
            float boneLineWidth,
            float axisLength,
            float axisLineWidth,
            Material handMaterial,
            Material axisXMaterial,
            Material axisYMaterial,
            Material axisZMaterial)
        {
            m_AxisLength = axisLength;

            var rootGo = new GameObject(name);
            m_Root = rootGo.transform;
            m_Root.SetParent(parent, false);

            // Joint spheres (one per MediaPipe landmark).
            m_Joints = new Transform[HandLandmarkMapping.LandmarkCount];
            for (int i = 0; i < m_Joints.Length; i++)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "Joint_" + i;
                Object.Destroy(sphere.GetComponent<Collider>());
                sphere.GetComponent<Renderer>().sharedMaterial = handMaterial;
                var t = sphere.transform;
                t.SetParent(m_Root, false);
                t.localScale = Vector3.one * (jointSphereRadius * 2f);
                m_Joints[i] = t;
            }

            // Bone lines.
            m_Bones = new LineRenderer[HandLandmarkMapping.BoneConnections.Length];
            for (int i = 0; i < m_Bones.Length; i++)
            {
                m_Bones[i] = CreateLine(m_Root, "Bone_" + i, handMaterial, boneLineWidth);
            }

            // Palm coordinate frame axes (X=red, Y=green, Z=blue).
            m_AxisX = CreateLine(m_Root, "PalmAxis_X", axisXMaterial, axisLineWidth);
            m_AxisY = CreateLine(m_Root, "PalmAxis_Y", axisYMaterial, axisLineWidth);
            m_AxisZ = CreateLine(m_Root, "PalmAxis_Z", axisZMaterial, axisLineWidth);

            SetVisible(false);
        }

        static LineRenderer CreateLine(Transform parent, string name, Material material, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.sharedMaterial = material;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.alignment = LineAlignment.View;
            return lr;
        }

        /// <summary>Enables/disables the whole hand visual (used to skip on tracking loss).</summary>
        public void SetVisible(bool visible)
        {
            if (m_Root.gameObject.activeSelf != visible)
                m_Root.gameObject.SetActive(visible);
        }

        /// <summary>Updates all visuals from the given unity_world frame.</summary>
        public void Render(HandLandmarkFrame frame)
        {
            // Joints.
            for (int i = 0; i < m_Joints.Length; i++)
            {
                bool valid = frame.valid[i];
                var jt = m_Joints[i];
                if (jt.gameObject.activeSelf != valid)
                    jt.gameObject.SetActive(valid);
                if (valid)
                    jt.position = frame.positions[i];
            }

            // Bones.
            var connections = HandLandmarkMapping.BoneConnections;
            for (int i = 0; i < m_Bones.Length; i++)
            {
                var (a, b) = connections[i];
                bool valid = frame.valid[a] && frame.valid[b];
                var lr = m_Bones[i];
                if (lr.enabled != valid)
                    lr.enabled = valid;
                if (valid)
                {
                    lr.SetPosition(0, frame.positions[a]);
                    lr.SetPosition(1, frame.positions[b]);
                }
            }

            // Palm coordinate frame (drawn in unity_world so it can be visually compared with
            // whatever pose the publish stage emits). Axis directions come from the palm orientation.
            if (frame.palmValid)
            {
                Vector3 o = frame.palmPose.position;
                Quaternion r = frame.palmPose.rotation;
                SetAxis(m_AxisX, o, o + r * Vector3.right * m_AxisLength);   // X = red
                SetAxis(m_AxisY, o, o + r * Vector3.up * m_AxisLength);      // Y = green
                SetAxis(m_AxisZ, o, o + r * Vector3.forward * m_AxisLength); // Z = blue
            }
            else
            {
                m_AxisX.enabled = false;
                m_AxisY.enabled = false;
                m_AxisZ.enabled = false;
            }
        }

        static void SetAxis(LineRenderer lr, Vector3 from, Vector3 to)
        {
            if (!lr.enabled)
                lr.enabled = true;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
        }
    }
}
