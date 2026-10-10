using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

namespace HandTrackingViz.EditorTools
{
    /// <summary>
    /// One-click scene setup for the hand-tracking visualization.
    /// Creates an XR Origin rig (with a headset-tracked camera), gives the arm-camera view an opaque
    /// background, and adds a wired-up <see cref="HandVisualizer"/>.
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

            // 2. The robotic-arm camera view uses an opaque black background.
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
            if (cam.GetComponent<PassthroughSetup>() == null)
                cam.gameObject.AddComponent<PassthroughSetup>();

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
            if (visualizer.leftControllerModelPrefab == null)
            {
                visualizer.leftControllerModelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Samples/XR Interaction Toolkit/3.2.2/Starter Assets/Prefabs/Controllers/XR Controller Left.prefab");
            }
            if (visualizer.rightControllerModelPrefab == null)
            {
                visualizer.rightControllerModelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Samples/XR Interaction Toolkit/3.2.2/Starter Assets/Prefabs/Controllers/XR Controller Right.prefab");
            }
            EditorUtility.SetDirty(visualizer);

            // 4b. Create / wire the PoseFramePublisher (HMD, hands and controllers over UDP).
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
            Debug.Log("[HandTrackingViz] Scene setup complete: XR Origin + arm-camera view + HandVisualizer.");
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
