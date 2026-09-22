using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.XR;
using InputSystem = UnityEngine.InputSystem.InputSystem;
using InputSystemCommonUsages = UnityEngine.InputSystem.CommonUsages;

namespace HandTrackingViz
{
    /// <summary>
    /// Lets either physical Quest controller move the arm-camera panel with a ray grab.
    /// Hold the controller's side grip while pointing at the panel, then release it
    /// to leave the panel at its new tracking-space position.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArmCameraWindowInteractor : MonoBehaviour
    {
        private const InputDeviceCharacteristics HeldController =
            InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Controller;
        private const InputDeviceCharacteristics HandProfile =
            InputDeviceCharacteristics.HandTracking;

        private readonly List<InputDevice> m_Devices = new List<InputDevice>();

        private Transform m_TrackingOrigin;
        private Collider m_WindowCollider;
        private LineRenderer m_LeftPointer;
        private LineRenderer m_RightPointer;
        private LineRenderer m_WindowBorder;
        private Material m_InteractionMaterial;

        private XRNode? m_ActiveController;
        private Vector3 m_LocalGrabPoint;
        private float m_GrabDistance;
        private bool m_LeftGripWasPressed;
        private bool m_RightGripWasPressed;

        private const float GrabThreshold = 0.65f;
        private const float PointerLength = 5f;
        private const float MinimumDistance = 0.35f;
        private const float MaximumDistance = 5f;
        private const float DepthSpeed = 1.2f;

        private static readonly Color IdlePointerColor = new Color(0.45f, 0.75f, 1f, 0.7f);
        private static readonly Color HoverPointerColor = new Color(0.2f, 1f, 0.45f, 1f);
        private static readonly Color GrabPointerColor = new Color(1f, 0.8f, 0.15f, 1f);

        public void Initialize(Transform trackingOrigin, Collider windowCollider)
        {
            m_TrackingOrigin = trackingOrigin;
            m_WindowCollider = windowCollider;
            CreatePointers();
        }

        void Update()
        {
            bool leftAvailable = TryGetController(InputDeviceCharacteristics.Left, out InputDevice leftDevice,
                                                  out Vector3 leftPosition, out Quaternion leftRotation);
            bool rightAvailable = TryGetController(InputDeviceCharacteristics.Right, out InputDevice rightDevice,
                                                   out Vector3 rightPosition, out Quaternion rightRotation);

            bool leftGripPressed = leftAvailable && IsGrabPressed(leftDevice);
            bool rightGripPressed = rightAvailable && IsGrabPressed(rightDevice);

            if (m_ActiveController == XRNode.LeftHand)
            {
                if (!leftAvailable || !leftGripPressed)
                    EndGrab();
                else
                    UpdateGrab(leftDevice, leftPosition, leftRotation);
            }
            else if (m_ActiveController == XRNode.RightHand)
            {
                if (!rightAvailable || !rightGripPressed)
                    EndGrab();
                else
                    UpdateGrab(rightDevice, rightPosition, rightRotation);
            }
            else
            {
                if (leftGripPressed && !m_LeftGripWasPressed)
                    TryBeginGrab(XRNode.LeftHand, leftPosition, leftRotation);
                else if (rightGripPressed && !m_RightGripWasPressed)
                    TryBeginGrab(XRNode.RightHand, rightPosition, rightRotation);
            }

            bool leftHovering = UpdatePointer(m_LeftPointer, XRNode.LeftHand,
                                              leftAvailable, leftPosition, leftRotation);
            bool rightHovering = UpdatePointer(m_RightPointer, XRNode.RightHand,
                                               rightAvailable, rightPosition, rightRotation);
            UpdateBorder(leftHovering || rightHovering, m_ActiveController.HasValue);

            m_LeftGripWasPressed = leftGripPressed;
            m_RightGripWasPressed = rightGripPressed;
        }

        bool TryGetController(InputDeviceCharacteristics side, out InputDevice device,
                              out Vector3 worldPosition, out Quaternion worldRotation)
        {
            device = default;
            worldPosition = default;
            worldRotation = Quaternion.identity;

            InputDevices.GetDevicesWithCharacteristics(HeldController | side, m_Devices);
            foreach (InputDevice candidate in m_Devices)
            {
                if (!candidate.isValid || (candidate.characteristics & HandProfile) != 0)
                    continue;
                if (!candidate.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked)
                    continue;
                bool hasPointerPose = TryGetPointerPose(side, out Vector3 localPosition,
                                                       out Quaternion localRotation);
                if (!hasPointerPose &&
                    (!candidate.TryGetFeatureValue(CommonUsages.devicePosition, out localPosition) ||
                     !candidate.TryGetFeatureValue(CommonUsages.deviceRotation, out localRotation)))
                    continue;

                device = candidate;
                worldPosition = m_TrackingOrigin != null
                    ? m_TrackingOrigin.TransformPoint(localPosition)
                    : localPosition;
                worldRotation = m_TrackingOrigin != null
                    ? m_TrackingOrigin.rotation * localRotation
                    : localRotation;
                return true;
            }

            return false;
        }

        static bool TryGetPointerPose(InputDeviceCharacteristics side,
                                      out Vector3 localPosition, out Quaternion localRotation)
        {
            localPosition = default;
            localRotation = Quaternion.identity;
            var handUsage = side == InputDeviceCharacteristics.Left
                ? InputSystemCommonUsages.LeftHand
                : InputSystemCommonUsages.RightHand;

            foreach (var candidate in InputSystem.devices)
            {
                if (!(candidate is XRController) || !candidate.enabled)
                    continue;

                bool matchesHand = false;
                foreach (var usage in candidate.usages)
                {
                    if (usage == handUsage)
                    {
                        matchesHand = true;
                        break;
                    }
                }
                if (!matchesHand)
                    continue;

                // A thumbstick distinguishes the physical Touch controller from
                // OpenXR hand-interaction devices that can also report pointer poses.
                if (candidate.TryGetChildControl<Vector2Control>("thumbstick") == null)
                    continue;

                ButtonControl tracked = candidate.TryGetChildControl<ButtonControl>("isTracked");
                if (tracked != null && !tracked.isPressed)
                    continue;

                Vector3Control position = candidate.TryGetChildControl<Vector3Control>("pointerPosition");
                QuaternionControl rotation = candidate.TryGetChildControl<QuaternionControl>("pointerRotation");
                if (position == null || rotation == null)
                    continue;

                localPosition = position.ReadValue();
                localRotation = rotation.ReadValue();
                return true;
            }

            return false;
        }

        static bool IsGrabPressed(InputDevice device)
        {
            if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool buttonPressed) && buttonPressed)
                return true;
            if (device.TryGetFeatureValue(CommonUsages.grip, out float gripValue) &&
                gripValue >= GrabThreshold)
                return true;
            if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerPressed) && triggerPressed)
                return true;
            return device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue) &&
                   triggerValue >= GrabThreshold;
        }

        void TryBeginGrab(XRNode controller, Vector3 position, Quaternion rotation)
        {
            if (m_WindowCollider == null)
                return;

            Ray ray = new Ray(position, rotation * Vector3.forward);
            if (!m_WindowCollider.Raycast(ray, out RaycastHit hit, PointerLength))
                return;

            m_ActiveController = controller;
            m_GrabDistance = Mathf.Clamp(hit.distance, MinimumDistance, MaximumDistance);
            m_LocalGrabPoint = transform.InverseTransformPoint(hit.point);
        }

        void UpdateGrab(InputDevice device, Vector3 position, Quaternion rotation)
        {
            if (device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 thumbstick))
            {
                m_GrabDistance = Mathf.Clamp(
                    m_GrabDistance + thumbstick.y * DepthSpeed * Time.unscaledDeltaTime,
                    MinimumDistance, MaximumDistance);
            }

            Vector3 targetGrabPoint = position + rotation * Vector3.forward * m_GrabDistance;
            Vector3 centerToGrabPoint = transform.TransformVector(m_LocalGrabPoint);
            transform.position = targetGrabPoint - centerToGrabPoint;
        }

        void EndGrab()
        {
            m_ActiveController = null;
        }

        bool UpdatePointer(LineRenderer pointer, XRNode controller, bool available,
                           Vector3 position, Quaternion rotation)
        {
            if (!available)
            {
                if (pointer != null)
                    pointer.enabled = false;
                return false;
            }

            Ray ray = new Ray(position, rotation * Vector3.forward);
            RaycastHit hit = default;
            bool hovering = m_WindowCollider != null &&
                            m_WindowCollider.Raycast(ray, out hit, PointerLength);
            bool grabbing = m_ActiveController == controller;
            float length = grabbing ? m_GrabDistance : hovering ? hit.distance : PointerLength;
            Color color = grabbing ? GrabPointerColor : hovering ? HoverPointerColor : IdlePointerColor;

            if (pointer != null)
            {
                pointer.enabled = true;
                SetLineColor(pointer, color);
                pointer.SetPosition(0, position);
                pointer.SetPosition(1, position + ray.direction * length);
            }
            return hovering;
        }

        void UpdateBorder(bool hovering, bool grabbing)
        {
            if (m_WindowBorder == null)
                return;
            SetLineColor(m_WindowBorder,
                grabbing ? GrabPointerColor : hovering ? HoverPointerColor : IdlePointerColor);
        }

        static void SetLineColor(LineRenderer line, Color color)
        {
            line.startColor = color;
            line.endColor = color;
        }

        void CreatePointers()
        {
            // Sprites/Default is part of Unity's always-included shaders and honors
            // LineRenderer vertex colors, so Android shader stripping cannot hide it.
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogError("[ArmCameraWindowInteractor] Pointer shader was not found.", this);
                return;
            }

            m_InteractionMaterial = new Material(shader) { name = "Arm camera interaction lines" };

            m_LeftPointer = CreatePointer("Left arm-camera pointer");
            m_RightPointer = CreatePointer("Right arm-camera pointer");
            m_WindowBorder = CreateBorder();
        }

        LineRenderer CreatePointer(string objectName)
        {
            GameObject pointerObject = new GameObject(objectName);
            pointerObject.layer = gameObject.layer;
            pointerObject.transform.SetParent(transform, false);

            LineRenderer pointer = pointerObject.AddComponent<LineRenderer>();
            pointer.useWorldSpace = true;
            pointer.positionCount = 2;
            pointer.widthMultiplier = 0.004f;
            pointer.numCapVertices = 4;
            pointer.alignment = LineAlignment.View;
            pointer.textureMode = LineTextureMode.Stretch;
            pointer.shadowCastingMode = ShadowCastingMode.Off;
            pointer.receiveShadows = false;
            pointer.sharedMaterial = m_InteractionMaterial;
            pointer.enabled = false;
            return pointer;
        }

        LineRenderer CreateBorder()
        {
            GameObject borderObject = new GameObject("Arm-camera window border");
            borderObject.layer = gameObject.layer;
            borderObject.transform.SetParent(transform, false);

            LineRenderer border = borderObject.AddComponent<LineRenderer>();
            border.useWorldSpace = false;
            border.loop = true;
            border.positionCount = 4;
            border.widthMultiplier = 0.008f;
            border.numCornerVertices = 3;
            border.numCapVertices = 3;
            border.shadowCastingMode = ShadowCastingMode.Off;
            border.receiveShadows = false;
            border.sharedMaterial = m_InteractionMaterial;
            border.SetPosition(0, new Vector3(-0.5f, -0.5f, -0.01f));
            border.SetPosition(1, new Vector3(-0.5f, 0.5f, -0.01f));
            border.SetPosition(2, new Vector3(0.5f, 0.5f, -0.01f));
            border.SetPosition(3, new Vector3(0.5f, -0.5f, -0.01f));
            SetLineColor(border, IdlePointerColor);
            return border;
        }

        void OnDestroy()
        {
            if (m_InteractionMaterial != null)
                Destroy(m_InteractionMaterial);
        }
    }
}
