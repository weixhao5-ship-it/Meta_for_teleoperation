using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HandTrackingViz
{
    // ====================================================================
    // ControllerSampler
    // --------------------------------------------------------------------
    // Samples both hand-held controllers each frame into ControllerData, as the
    // controller-side counterpart to the hand sampling in PoseFramePublisher.
    // Plain C# (not a MonoBehaviour) — PoseFramePublisher owns one and calls
    // Fill() from its per-frame capture.
    //
    // Source: the legacy UnityEngine.XR.InputDevices API, which OpenXR feeds
    // from whichever controller interaction profile the runtime bound. That
    // keeps this file profile-agnostic: enabling a different profile under
    // XR Plug-in Management -> OpenXR -> Interaction Profiles changes what the
    // runtime binds, not this code. (This project ships with the Oculus Touch
    // Controller Profile enabled for Android.)
    //
    // Frame: device poses come back in the XR Origin's TRACKING space, same as
    // XR Hands joints, so they are converted through xrOriginTransform into
    // Unity world space (PoseFramePublisher converts to the robot_world wire
    // frame at serialization). A controller that is absent or untracked is reported
    // tracked = false rather than holding a stale pose.
    // ====================================================================
    public sealed class ControllerSampler
    {
        private const InputDeviceCharacteristics HeldController =
            InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Controller;

        // HeldInHand|Controller is NOT enough to identify a physical controller.
        // Hand-tracking interaction profiles also register as controllers (this
        // project has Hand Common Poses Interaction enabled; measured on Android
        // XR the same way):
        //
        //   'Hand Interaction Poses OpenXR' [HeldInHand, HandTracking, TrackedDevice, Controller, Left]
        //   'Palm Pose Interaction OpenXR'  [HeldInHand, HandTracking, TrackedDevice, Controller, Left]
        //
        // Matching those yields a plausible grip pose (it is the hand's) with
        // trigger/squeeze/thumbstick pinned at 0 forever, which reads downstream
        // as "controller present but never pressed" — silently wrong. A real
        // controller never carries HandTracking, so exclude it.
        private const InputDeviceCharacteristics NotAController =
            InputDeviceCharacteristics.HandTracking;

        // Reused across frames — GetDevicesWithCharacteristics clears and refills.
        private readonly List<InputDevice> scratch = new List<InputDevice>();

        // Fills both controllers. `xrOriginTransform` may be null, in which case
        // tracking space is assumed to already be world space (matches the hand
        // path's own fallback).
        public void Fill(ControllerData left, ControllerData right, Transform xrOriginTransform)
        {
            FillSide(InputDeviceCharacteristics.Left, left, xrOriginTransform);
            FillSide(InputDeviceCharacteristics.Right, right, xrOriginTransform);
        }

        // Marks both controllers untracked — used when the XR subsystem is not
        // up yet, so a frame is still published with an explicit "no controller"
        // rather than a stale one.
        public static void MarkUntracked(ControllerData left, ControllerData right)
        {
            left.tracked = false;
            right.tracked = false;
        }

        private void FillSide(InputDeviceCharacteristics side, ControllerData data, Transform origin)
        {
            InputDevices.GetDevicesWithCharacteristics(HeldController | side, scratch);

            // Take the first match that is a real controller, not a hand-tracking
            // profile masquerading as one (see NotAController).
            InputDevice device = default;
            bool found = false;
            foreach (var candidate in scratch)
            {
                if (!candidate.isValid) continue;
                if ((candidate.characteristics & NotAController) != 0) continue;
                device = candidate;
                found = true;
                break;
            }
            if (!found) { data.tracked = false; return; }

            // isTracked is the runtime's own "this pose is live" flag. A connected
            // but untracked controller (set down, occluded, out of range) reports
            // false, and we publish that rather than its last known pose.
            if (!device.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) || !isTracked)
            {
                data.tracked = false;
                return;
            }

            // The handle pose gates the whole controller: without it there is
            // nothing an end-effector can follow, so report it untracked.
            if (!TryGetPose(device, CommonUsages.devicePosition, CommonUsages.deviceRotation,
                            origin, out PoseSE3 pose))
            {
                data.tracked = false;
                return;
            }
            data.pose = pose;

            // Wire names are teleop-facing (see ControllerData): gripTrigger is
            // the INDEX trigger (drives the gripper downstream), ctrlTrigger is
            // the hardware grip/squeeze axis (CommonUsages.grip).
            data.gripTrigger = GetFloat(device, CommonUsages.trigger);
            data.ctrlTrigger = GetFloat(device, CommonUsages.grip);
            data.thumbstick = GetVec2(device, CommonUsages.primary2DAxis);

            data.primaryButton = GetBool(device, CommonUsages.primaryButton);
            data.secondaryButton = GetBool(device, CommonUsages.secondaryButton);
            data.menuButton = GetBool(device, CommonUsages.menuButton);
            data.thumbstickClicked = GetBool(device, CommonUsages.primary2DAxisClick);

            data.tracked = true;
        }

        // Reads a position+rotation pair and converts it from tracking space into
        // Unity world space. Both halves must resolve for the pose to count as valid.
        private static bool TryGetPose(InputDevice device,
                                       InputFeatureUsage<Vector3> positionUsage,
                                       InputFeatureUsage<Quaternion> rotationUsage,
                                       Transform origin,
                                       out PoseSE3 pose)
        {
            if (device.TryGetFeatureValue(positionUsage, out Vector3 localPos) &&
                device.TryGetFeatureValue(rotationUsage, out Quaternion localRot))
            {
                pose = new PoseSE3
                {
                    tracked = true,
                    position = origin != null ? origin.TransformPoint(localPos) : localPos,
                    rotation = origin != null ? origin.rotation * localRot : localRot
                };
                return true;
            }

            pose = PoseSE3.NotTracked;
            return false;
        }

        // Inputs a given profile does not expose read as neutral (0 / false)
        // rather than failing the whole controller — a profile without a
        // thumbstick should still stream its trigger and poses.
        private static float GetFloat(InputDevice device, InputFeatureUsage<float> usage)
            => device.TryGetFeatureValue(usage, out float v) ? v : 0f;

        private static Vector2 GetVec2(InputDevice device, InputFeatureUsage<Vector2> usage)
            => device.TryGetFeatureValue(usage, out Vector2 v) ? v : Vector2.zero;

        private static bool GetBool(InputDevice device, InputFeatureUsage<bool> usage)
            => device.TryGetFeatureValue(usage, out bool v) && v;
    }
}
