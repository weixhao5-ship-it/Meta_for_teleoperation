using UnityEngine;

namespace HandTrackingViz
{
    /// <summary>
    /// Minimal passthrough enablement for Meta Quest via Unity's Meta OpenXR support.
    ///
    /// The actual passthrough image is composited by the OpenXR runtime once the
    /// "Meta Quest: Passthrough" OpenXR feature is enabled in
    /// Project Settings ▸ XR Plug-in Management ▸ OpenXR (Android).
    ///
    /// Attach this to the Main Camera under the XR Origin. Set showPassthrough to select either
    /// passthrough behind virtual content or an opaque background for the arm-camera video screen.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class PassthroughSetup : MonoBehaviour
    {
        [Tooltip("Show the Quest passthrough background. Disable this when using the arm-camera video view.")]
        public bool showPassthrough;

        Camera m_Camera;

        void OnEnable()
        {
            m_Camera = GetComponent<Camera>();

            // Alpha 0 reveals the OpenXR passthrough layer; alpha 1 gives the arm-camera view a black background.
            m_Camera.clearFlags = CameraClearFlags.SolidColor;
            m_Camera.backgroundColor = new Color(0f, 0f, 0f, showPassthrough ? 0f : 1f);

            // On URP the per-camera "Background Type" also needs to be Solid Color; setting clearFlags
            // above keeps the camera in sync. If you use a skybox anywhere, disable it for this camera.
        }
    }
}
