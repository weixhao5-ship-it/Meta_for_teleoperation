using UnityEngine;
using UnityEngine.XR.Hands;

namespace HandTrackingViz
{
    /// <summary>Which physical hand a landmark frame belongs to.</summary>
    public enum HandSide
    {
        Left,
        Right
    }

    /// <summary>
    /// MediaPipe-style 21 hand landmark indices.
    /// This ordering is the de-facto standard used by MediaPipe Hands and is what the
    /// future ZMQ publish stage is expected to emit, so we compute/store data in this order.
    /// </summary>
    public enum HandLandmark
    {
        Wrist = 0,
        ThumbCmc = 1,
        ThumbMcp = 2,
        ThumbIp = 3,
        ThumbTip = 4,
        IndexMcp = 5,
        IndexPip = 6,
        IndexDip = 7,
        IndexTip = 8,
        MiddleMcp = 9,
        MiddlePip = 10,
        MiddleDip = 11,
        MiddleTip = 12,
        RingMcp = 13,
        RingPip = 14,
        RingDip = 15,
        RingTip = 16,
        PinkyMcp = 17,
        PinkyPip = 18,
        PinkyDip = 19,
        PinkyTip = 20
    }

    /// <summary>
    /// One frame of hand data expressed in the <b>unity_world</b> coordinate system
    /// (left-handed, Y-up). Rotations are Unity quaternions whose memory order is
    /// (x, y, z, w) — matching the order the downstream publisher expects.
    ///
    /// This type is intentionally free of any rendering concerns so it can be reused
    /// directly by the future ZMQ publish stage.
    /// </summary>
    public sealed class HandLandmarkFrame
    {
        public HandSide side;

        /// <summary>True when the whole hand is currently tracked this frame.</summary>
        public bool isTracked;

        /// <summary>Landmark positions in unity_world. Length == <see cref="HandLandmarkMapping.LandmarkCount"/>.</summary>
        public readonly Vector3[] positions = new Vector3[HandLandmarkMapping.LandmarkCount];

        /// <summary>Landmark rotations in unity_world (quaternion x,y,z,w). Same length as positions.</summary>
        public readonly Quaternion[] rotations = new Quaternion[HandLandmarkMapping.LandmarkCount];

        /// <summary>Per-landmark validity (a joint may be missing even when the hand is tracked).</summary>
        public readonly bool[] valid = new bool[HandLandmarkMapping.LandmarkCount];

        /// <summary>Palm middle pose in unity_world (see <see cref="HandLandmarkProvider"/> for its definition).</summary>
        public Pose palmPose;

        /// <summary>True when <see cref="palmPose"/> was computed this frame.</summary>
        public bool palmValid;

        public HandLandmarkFrame(HandSide side)
        {
            this.side = side;
        }
    }

    /// <summary>
    /// Static mapping tables between OpenXR (Unity XR Hands) joints and the MediaPipe 21-landmark model,
    /// plus the MediaPipe bone connection list. Kept separate so both the calculation and rendering
    /// code share a single source of truth.
    /// </summary>
    public static class HandLandmarkMapping
    {
        public const int LandmarkCount = 21;

        /// <summary>
        /// Maps each MediaPipe landmark index to the corresponding Unity XR Hands (OpenXR) joint.
        ///
        /// Notes on the mapping:
        /// - OpenXR reports 26 joints per hand (incl. Palm and per-finger Metacarpal joints that
        ///   MediaPipe does not have). We select the 21 that correspond to the MediaPipe model.
        /// - MediaPipe "MCP" (knuckle) == OpenXR "Proximal"; OpenXR "Metacarpal" sits closer to the wrist
        ///   and is intentionally skipped for the four fingers.
        /// - The thumb has no intermediate phalanx, so MediaPipe thumb joints map onto
        ///   OpenXR Metacarpal/Proximal/Distal/Tip.
        /// </summary>
        public static readonly XRHandJointID[] LandmarkToXRJoint =
        {
            XRHandJointID.Wrist,            // 0  Wrist
            XRHandJointID.ThumbMetacarpal,  // 1  Thumb CMC
            XRHandJointID.ThumbProximal,    // 2  Thumb MCP
            XRHandJointID.ThumbDistal,      // 3  Thumb IP
            XRHandJointID.ThumbTip,         // 4  Thumb TIP
            XRHandJointID.IndexProximal,    // 5  Index MCP
            XRHandJointID.IndexIntermediate,// 6  Index PIP
            XRHandJointID.IndexDistal,      // 7  Index DIP
            XRHandJointID.IndexTip,         // 8  Index TIP
            XRHandJointID.MiddleProximal,   // 9  Middle MCP
            XRHandJointID.MiddleIntermediate,//10 Middle PIP
            XRHandJointID.MiddleDistal,     // 11 Middle DIP
            XRHandJointID.MiddleTip,        // 12 Middle TIP
            XRHandJointID.RingProximal,     // 13 Ring MCP
            XRHandJointID.RingIntermediate, // 14 Ring PIP
            XRHandJointID.RingDistal,       // 15 Ring DIP
            XRHandJointID.RingTip,          // 16 Ring TIP
            XRHandJointID.LittleProximal,   // 17 Pinky MCP
            XRHandJointID.LittleIntermediate,//18 Pinky PIP
            XRHandJointID.LittleDistal,     // 19 Pinky DIP
            XRHandJointID.LittleTip         // 20 Pinky TIP
        };

        /// <summary>
        /// Standard MediaPipe hand skeleton connections (21 bones), expressed as pairs of
        /// landmark indices. Used to draw bone lines between joints.
        /// </summary>
        public static readonly (int a, int b)[] BoneConnections =
        {
            // Thumb
            (0, 1), (1, 2), (2, 3), (3, 4),
            // Index
            (0, 5), (5, 6), (6, 7), (7, 8),
            // Middle
            (5, 9), (9, 10), (10, 11), (11, 12),
            // Ring
            (9, 13), (13, 14), (14, 15), (15, 16),
            // Pinky
            (13, 17), (17, 18), (18, 19), (19, 20),
            // Palm arch closing the ring back to the wrist
            (0, 17)
        };
    }
}
