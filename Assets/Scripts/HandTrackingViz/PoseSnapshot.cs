using System;
using UnityEngine;

namespace HandTrackingViz
{
    // SE(3) pose. Frame: Unity world space — LEFT-HANDED, Y-up, meters.
    // OpenXR's reference space is mapped into Unity world space by the XR
    // Origin, so everything held IN MEMORY here is Unity world coordinates.
    // PoseFramePublisher converts to the right-handed "robot_world" wire frame
    // (+X forward, +Y left, +Z up) at serialization time, so consumers never
    // see Unity coordinates — see SCHEMA.md.
    [Serializable]
    public struct PoseSE3
    {
        public bool tracked;
        public Vector3 position;     // meters, Unity world space
        public Quaternion rotation;  // Unity quaternion, order (x, y, z, w)

        public static PoseSE3 NotTracked => new PoseSE3
        {
            tracked = false,
            position = Vector3.zero,
            rotation = Quaternion.identity
        };
    }

    // One controller's payload for the current frame: the handle pose + the
    // analog and digital inputs. `tracked` gates the whole controller — when
    // false it is serialized as {"side": ..., "tracked": false} with nothing
    // else, exactly like an untracked hand.
    //
    // NAMING IS TELEOP-FACING, NOT OpenXR-FACING. The wire names describe what
    // the receiver does with each input, so they deliberately differ from
    // OpenXR's input paths:
    //
    //   JSON            hardware input              OpenXR path
    //   "controller"    handle 6DoF pose            /input/grip/pose
    //   "grip_trigger"  INDEX (front) trigger       /input/trigger/value
    //   "ctrl_trigger"  hardware grip (side) axis   /input/squeeze/value
    //
    // "grip_trigger" is named for its ROLE (it drives the robot gripper), not
    // for the hardware grip button — see the controllers section in SCHEMA.md.
    public sealed class ControllerData
    {
        public bool tracked;

        // The controller's handle pose (OpenXR grip pose) — the natural
        // counterpart to a hand's palm pose, and what an arm end-effector
        // should follow. Serialized as "controller".
        public PoseSE3 pose;

        public float gripTrigger;                // index trigger, 0..1 ("grip_trigger")
        public float ctrlTrigger;                // hardware grip/squeeze axis, 0..1 ("ctrl_trigger")
        public Vector2 thumbstick;               // /input/thumbstick, each axis -1..1

        public bool primaryButton;               // A / X
        public bool secondaryButton;             // B / Y
        public bool menuButton;
        public bool thumbstickClicked;
    }
}
