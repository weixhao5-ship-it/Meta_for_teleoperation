using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.ARFoundation;

namespace HandTrackingViz.EditorTools
{
    /// <summary>
    /// One-click scene setup for the hand-tracking visualization.
    /// Creates an XR Origin rig (with a headset-tracked camera), makes the camera transparent for
    /// passthrough, and adds a wired-up <see cref="HandVisualizer"/>.
    ///
    /// Run it from the menu: <b>Tools ▸ Hand Tracking Viz ▸ 1. Setup Scene</b>.
    /// Requires the XR packages (OpenXR / XR Hands / XR CoreUtils) to be installed so the types resolve.
    /// </summary>
    public static class HandTrackingVizSceneSetup
    {
        [MenuItem("Tools/Hand Tracking Viz/1. Setup Scene")]
        public static void SetupScene()
        {
            // 1. Reuse an existing XR Origin if the scene already has one, otherwise build a rig.
            var origin = Object.FindFirstObjectByType<XROrigin>();
            Camera cam;

            if (origin != null && origin.Camera != null)
            {
                cam = origin.Camera;
            }
            else
            {
                origin = BuildXrOriginRig(out cam);
            }

            // 2. Camera must clear transparent so the OpenXR passthrough layer shows through.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            if (cam.GetComponent<PassthroughSetup>() == null)
                cam.gameObject.AddComponent<PassthroughSetup>();

            // 2b. Meta Passthrough is delivered through AR Foundation's camera subsystem. Without these,
            //     the passthrough subsystem never starts and the transparent clear shows up as solid black:
            //       - ARCameraManager on the camera actually starts/stops Passthrough,
            //       - ARSession drives the AR session lifecycle (uses the "Meta Quest: Session" feature).
            //     (ARCameraBackground is intentionally NOT added — Meta passthrough is a compositor layer
            //      and does not use it.)
            if (cam.GetComponent<ARCameraManager>() == null)
                cam.gameObject.AddComponent<ARCameraManager>();
            EnsureARSession();

            // 2c. URP HDR must be OFF for passthrough, otherwise the HDR color buffer covers the
            //     passthrough layer (black screen). See Meta OpenXR "Optimize graphics settings".
            DisableHdrOnAllUrpAssets();

            // 3. Remove any leftover default Main Camera that is not part of an XR rig (avoids
            //    duplicate cameras / AudioListeners).
            RemoveStrayMainCameras(cam);

            // 4. Create / wire the HandVisualizer.
            var visualizer = Object.FindFirstObjectByType<HandVisualizer>();
            if (visualizer == null)
            {
                var go = new GameObject("HandVisualizer");
                Undo.RegisterCreatedObjectUndo(go, "Create HandVisualizer");
                visualizer = go.AddComponent<HandVisualizer>();
            }
            visualizer.trackingOrigin = origin.transform;
            EditorUtility.SetDirty(visualizer);

            // 4b. Create / wire the PoseFramePublisher (data-only: hands + palm + HMD pose over TCP/NDJSON).
            var publisher = Object.FindFirstObjectByType<PoseFramePublisher>();
            if (publisher == null)
            {
                var go = new GameObject("PosePublisher");
                Undo.RegisterCreatedObjectUndo(go, "Create PosePublisher");
                publisher = go.AddComponent<PoseFramePublisher>();
            }
            publisher.trackingOrigin = origin.transform;
            publisher.hmdCamera = cam;
            EditorUtility.SetDirty(publisher);

            // 5. Persist.
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Selection.activeObject = visualizer;
            Debug.Log("[HandTrackingViz] Scene setup complete: XR Origin + passthrough camera + HandVisualizer.");
        }

        static XROrigin BuildXrOriginRig(out Camera cam)
        {
            var originGo = new GameObject("XR Origin");
            Undo.RegisterCreatedObjectUndo(originGo, "Create XR Origin");
            var origin = originGo.AddComponent<XROrigin>();

            var offsetGo = new GameObject("Camera Offset");
            offsetGo.transform.SetParent(originGo.transform, false);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(offsetGo.transform, false);
            cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();

            // Drive the camera from the HMD via the Input System's TrackedPoseDriver.
            var driver = camGo.AddComponent<TrackedPoseDriver>();
            driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            driver.positionInput = new InputActionProperty(new InputAction(
                "HMD Position", InputActionType.Value, "<XRHMD>/centerEyePosition",
                expectedControlType: "Vector3"));
            driver.rotationInput = new InputActionProperty(new InputAction(
                "HMD Rotation", InputActionType.Value, "<XRHMD>/centerEyeRotation",
                expectedControlType: "Quaternion"));

            origin.Camera = cam;
            origin.CameraFloorOffsetObject = offsetGo;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;

            return origin;
        }

        static void EnsureARSession()
        {
            if (Object.FindFirstObjectByType<ARSession>() != null)
                return; // exactly one ARSession is required; reuse the existing one

            var go = new GameObject("AR Session");
            Undo.RegisterCreatedObjectUndo(go, "Create AR Session");
            go.AddComponent<ARSession>();
        }

        /// <summary>
        /// Turns off HDR on every URP asset in the project. A HDR color buffer prevents the transparent
        /// camera clear from compositing with the Meta passthrough layer, producing a black background.
        /// </summary>
        static void DisableHdrOnAllUrpAssets()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Object asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null)
                    continue;

                var so = new SerializedObject(asset);
                SerializedProperty prop = so.FindProperty("m_SupportsHDR");
                if (prop != null && prop.boolValue)
                {
                    prop.boolValue = false;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(asset);
                    Debug.Log($"[HandTrackingViz] Disabled HDR on URP asset '{path}' (required for Passthrough).");
                }
            }
        }

        static void RemoveStrayMainCameras(Camera keep)
        {
            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (c == keep)
                    continue;
                if (c.GetComponentInParent<XROrigin>() != null)
                    continue; // belongs to an XR rig, leave it
                if (c.CompareTag("MainCamera"))
                    Object.DestroyImmediate(c.gameObject);
            }
        }
    }
}
