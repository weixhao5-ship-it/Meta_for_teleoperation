using UnityEngine;

namespace HandTrackingViz
{
    // ====================================================================
    // StatusIndicator
    // --------------------------------------------------------------------
    // Head-locked HUD of four 0.5 cm dots pinned to the top-left of the
    // operator's view. Fixed order, left to right:
    //
    //   1. HMD      — head pose is live this frame
    //   2. LEFT     — left hand OR left controller tracked
    //   3. RIGHT    — right hand OR right controller tracked
    //   4. RECEIVER — at least one UDP subscriber is registered (set apart by
    //                 an extra gap, marking it as the network dot)
    //
    // GREEN = OK, RED = missing. Source dots reflect the latest tracked values
    // serialized by PoseFramePublisher; RECEIVER means a UDP endpoint registered.
    //
    // The dots are tiny spheres parented to the HMD camera (head-locked), so
    // they need no billboarding and stay in the corner of the view.
    // ====================================================================
    public sealed class StatusIndicator : MonoBehaviour
    {
        private const int DotCount = 4;
        private const float Distance = 0.5f;         // m in front of the camera
        private const float DotDiameter = 0.005f;    // 0.5 cm
        private const float Spacing = 0.012f;        // dot centre-to-centre
        private const float GroupGap = 0.006f;       // extra gap before RECEIVER
        private static readonly Vector3 TopLeft = new Vector3(-0.14f, 0.09f, Distance);

        private static readonly Color Green = new Color(0.1f, 0.9f, 0.2f);
        private static readonly Color Red = new Color(0.95f, 0.15f, 0.1f);

        private static readonly string[] DotNames =
            { "Dot_HMD", "Dot_Left", "Dot_Right", "Dot_Receiver" };

        private readonly Renderer[] dots = new Renderer[DotCount];
        private readonly bool[] states = new bool[DotCount];
        private Material greenMat;
        private Material redMat;

        // Creates the HUD as a head-locked child of the HMD camera.
        public static StatusIndicator Attach(Transform hmdCamera)
        {
            var go = new GameObject("StatusHUD");
            go.transform.SetParent(hmdCamera, false);
            return go.AddComponent<StatusIndicator>();
        }

        private void Awake()
        {
            // Unlit so the colors read over passthrough (same shader choice as
            // HandVisualizer's fallback). Two shared materials; dots swap
            // between them instead of mutating per-dot materials.
            greenMat = MakeUnlit(Green);
            redMat = MakeUnlit(Red);

            for (int i = 0; i < DotCount; i++)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = DotNames[i];
                sphere.transform.SetParent(transform, false);
                float x = TopLeft.x + i * Spacing + (i == DotCount - 1 ? GroupGap : 0f);
                sphere.transform.localPosition = new Vector3(x, TopLeft.y, TopLeft.z);
                sphere.transform.localScale = Vector3.one * DotDiameter;

                // Colliders are useless here and just cost physics work.
                var col = sphere.GetComponent<Collider>();
                if (col != null) Destroy(col);

                dots[i] = sphere.GetComponent<Renderer>();
                dots[i].sharedMaterial = redMat;   // pessimistic until first update
            }
        }

        private static Material MakeUnlit(Color c)
        {
            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.color = c;
            return mat;
        }

        // Called once per frame by PoseFramePublisher, AFTER the snapshot has
        // been published, with the published tracked flags + client count.
        public void SetStates(bool hmd, bool left, bool right, bool receiver)
        {
            Apply(0, hmd);
            Apply(1, left);
            Apply(2, right);
            Apply(3, receiver);
        }

        private void Apply(int i, bool ok)
        {
            if (states[i] == ok || dots[i] == null) return;
            states[i] = ok;
            dots[i].sharedMaterial = ok ? greenMat : redMat;
        }
    }
}
