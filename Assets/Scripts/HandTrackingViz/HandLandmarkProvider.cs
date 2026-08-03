using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace HandTrackingViz
{
    /// <summary>
    /// Pure calculation logic for hand landmarks and palm pose. Contains NO rendering code so it
    /// can be reused unchanged by the future ZMQ publish stage.
    ///
    /// Responsibilities:
    ///  - Acquire the running <see cref="XRHandSubsystem"/> (OpenXR hand tracking).
    ///  - Read the 21 MediaPipe joints from OpenXR and express them in unity_world.
    ///  - Compute the palm middle pose in unity_world.
    ///
    /// Coordinate system: all outputs are in unity_world (left-handed, Y-up). XR Hands reports joint
    /// poses relative to the XR Origin (tracking space); we convert them to world using the supplied
    /// tracking-origin transform. Quaternions follow Unity's (x, y, z, w) memory order.
    /// </summary>
    public sealed class HandLandmarkProvider
    {
        /// <summary>How the palm middle pose is defined.</summary>
        public enum PalmPoseSource
        {
            /// <summary>
            /// Use OpenXR's dedicated palm joint (XR_HAND_JOINT_PALM_EXT / <see cref="XRHandJointID.Palm"/>).
            /// The runtime reports a pose centered in the middle of the palm with an orientation that
            /// tracks the palm plane. This is the default and the recommended definition.
            /// </summary>
            OpenXRPalmJoint,

            /// <summary>
            /// Fallback definition when the palm joint is unavailable: position = midpoint of the wrist
            /// and the middle-finger MCP (OpenXR MiddleProximal); orientation is taken from the wrist joint.
            /// </summary>
            WristAndMiddleMcp
        }

        public PalmPoseSource palmPoseSource = PalmPoseSource.OpenXRPalmJoint;

        static readonly List<XRHandSubsystem> s_SubsystemsReuse = new List<XRHandSubsystem>();
        XRHandSubsystem m_Subsystem;

        /// <summary>True when a running OpenXR hand-tracking subsystem is available.</summary>
        public bool HasSubsystem => m_Subsystem != null && m_Subsystem.running;

        /// <summary>Finds and caches a running <see cref="XRHandSubsystem"/>. Returns false if none exists.</summary>
        public bool EnsureSubsystem()
        {
            if (m_Subsystem != null && m_Subsystem.running)
                return true;

            m_Subsystem = null;
            SubsystemManager.GetSubsystems(s_SubsystemsReuse);
            for (int i = 0; i < s_SubsystemsReuse.Count; i++)
            {
                if (s_SubsystemsReuse[i].running)
                {
                    m_Subsystem = s_SubsystemsReuse[i];
                    break;
                }
            }
            return m_Subsystem != null;
        }

        /// <summary>
        /// Fills <paramref name="frame"/> with the current unity_world landmarks and palm pose for the
        /// given hand. Returns false (and marks the frame untracked) when the hand is not tracked this
        /// frame — callers should skip rendering/publishing that hand on a false result.
        /// </summary>
        /// <param name="side">Which hand to read.</param>
        /// <param name="trackingOrigin">
        /// The XR Origin transform that XR Hands poses are relative to. When null, poses are treated as
        /// already being in world space.
        /// </param>
        /// <param name="frame">Reusable output frame (allocated once by the caller, refilled each frame).</param>
        public bool TryUpdate(HandSide side, Transform trackingOrigin, HandLandmarkFrame frame)
        {
            frame.side = side;
            frame.isTracked = false;
            frame.palmValid = false;

            if (!EnsureSubsystem())
                return false;

            XRHand hand = side == HandSide.Left ? m_Subsystem.leftHand : m_Subsystem.rightHand;
            if (!hand.isTracked)
                return false; // tracking loss for this hand

            // Fill the 21 MediaPipe landmarks in unity_world.
            for (int i = 0; i < HandLandmarkMapping.LandmarkCount; i++)
            {
                XRHandJointID jointId = HandLandmarkMapping.LandmarkToXRJoint[i];
                XRHandJoint joint = hand.GetJoint(jointId);

                if (joint.TryGetPose(out Pose localPose))
                {
                    ToWorld(trackingOrigin, localPose, out frame.positions[i], out frame.rotations[i]);
                    frame.valid[i] = true;
                }
                else
                {
                    frame.valid[i] = false;
                }
            }

            // Palm middle pose (definition depends on palmPoseSource).
            frame.palmValid = TryComputePalmPose(hand, trackingOrigin, out frame.palmPose);

            frame.isTracked = true;
            return true;
        }

        /// <summary>
        /// Computes the palm middle pose in unity_world according to <see cref="palmPoseSource"/>.
        /// Exposed publicly so the publish stage can reuse the exact same definition.
        /// </summary>
        public bool TryComputePalmPose(XRHand hand, Transform trackingOrigin, out Pose palmWorld)
        {
            palmWorld = Pose.identity;

            if (palmPoseSource == PalmPoseSource.OpenXRPalmJoint)
            {
                // XR_HAND_JOINT_PALM_EXT: palm-center pose provided directly by the OpenXR runtime.
                XRHandJoint palm = hand.GetJoint(XRHandJointID.Palm);
                if (palm.TryGetPose(out Pose palmLocal))
                {
                    ToWorld(trackingOrigin, palmLocal, out Vector3 p, out Quaternion r);
                    palmWorld = new Pose(p, r);
                    return true;
                }
                return false;
            }

            // WristAndMiddleMcp fallback:
            //   position    = midpoint(wrist, middle-finger MCP == OpenXR MiddleProximal)
            //   orientation = wrist joint orientation
            XRHandJoint wrist = hand.GetJoint(XRHandJointID.Wrist);
            XRHandJoint middleMcp = hand.GetJoint(XRHandJointID.MiddleProximal);
            if (wrist.TryGetPose(out Pose wristLocal) && middleMcp.TryGetPose(out Pose middleLocal))
            {
                ToWorld(trackingOrigin, wristLocal, out Vector3 wristPos, out Quaternion wristRot);
                ToWorld(trackingOrigin, middleLocal, out Vector3 middlePos, out _);
                Vector3 center = 0.5f * (wristPos + middlePos);
                palmWorld = new Pose(center, wristRot);
                return true;
            }

            return false;
        }

        /// <summary>Converts an XR Hands (tracking-space) pose into unity_world.</summary>
        static void ToWorld(Transform origin, Pose local, out Vector3 worldPos, out Quaternion worldRot)
        {
            if (origin == null)
            {
                worldPos = local.position;
                worldRot = local.rotation;
                return;
            }

            worldPos = origin.TransformPoint(local.position);
            worldRot = origin.rotation * local.rotation;
        }
    }
}
