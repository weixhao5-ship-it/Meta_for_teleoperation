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
    /// All this component does is make the camera clear to fully transparent so the passthrough feed
    /// shows through as the background, with every visualization rendered on top. Attach it to the
    /// Main Camera (the one under the XR Origin).
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class PassthroughSetup : MonoBehaviour
    {
        Camera m_Camera;

        void OnEnable()
        {
            m_Camera = GetComponent<Camera>();

            // Solid color clear with alpha 0 => the OpenXR passthrough layer shows behind our content.
            m_Camera.clearFlags = CameraClearFlags.SolidColor;
            m_Camera.backgroundColor = new Color(0f, 0f, 0f, 0f);

            // On URP the per-camera "Background Type" also needs to be Solid Color; setting clearFlags
            // above keeps the camera in sync. If you use a skybox anywhere, disable it for this camera.
        }
    }
}
