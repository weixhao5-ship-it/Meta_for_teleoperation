"""
Meta Quest unified subscriber (UDP / JSON datagrams).

The PC sends a small registration datagram to the Unity app on the headset
(default port 5555). Unity then sends one UTF-8 JSON datagram per pose update
to the PC's UDP source endpoint. The subscriber repeats registration once per
second, so Unity can discover the PC address without a separate IP setting.
This script prints the receive rate and HMD / hand / controller tracking state.
It uses only Python's standard library.

One datagram carries the whole device state:
  {
    "timestamp": <seconds since app start, double>,
    "frame": <Unity frame count, int>,
    "coordinate_system": "robot_world",
    "hmd": { "tracked": true, "position": [x,y,z], "rotation": [x,y,z,w] },
    "hands": [
      { "side": "left", "tracked": true,
        "palm": { "valid": true, "position": [x,y,z], "rotation": [x,y,z,w] },
        "landmarks": [ { "i": 0, "valid": true,
                         "position": [x,y,z], "rotation": [x,y,z,w] }, ... x21 ] },
      { "side": "right", "tracked": false }
    ],
    "controllers": [
      { "side": "left", "tracked": true,
        "controller": { "valid": true, "position": [x,y,z], "rotation": [x,y,z,w] },
        "grip_trigger": 0.85, "ctrl_trigger": 0.12, "thumbstick": [x, y],
        "buttons": { "primary": false, "secondary": false,
                     "menu": false, "thumbstick": false } },
      { "side": "right", "tracked": false }
    ]
  }

Hands vs controllers: the two are INDEPENDENT, not a mode switch. Any packet may
carry both, either, or neither — pick whichever source is tracked. "controller"
is the handle POSE; "grip_trigger" is the INDEX (front) trigger — named for its
role, it drives the gripper — and "ctrl_trigger" is the hardware grip (side)
analog axis.

Frame: every pose/landmark is in the "robot_world" frame — RIGHT-HANDED,
+X forward, +Y left, +Z up (FLU, ROS REP-103 style), meters. The publisher
converts from Unity's left-handed Y-up world at serialization time, so no
conversion is needed on this (receiver) side. Quaternion (rotation) order is
(x, y, z, w). +X forward is the user's facing direction at app start/recenter.

Tracking loss:
  - An untracked hand is {"side": ..., "tracked": false} (no palm/landmarks).
  - An untracked controller is {"side": ..., "tracked": false} (nothing else).
  - An untracked HMD is {"tracked": false} (no position/rotation).
  - A landmark, palm or controller pose with "valid": false omits its
    position/rotation.

Usage:
    python subscriber.py --host 192.168.0.42 --port 5555
"""

import argparse
import json
import socket
import time
from typing import Optional

LANDMARKS_PER_HAND = 21
SUBSCRIBE_MESSAGE = b"META_POSE_SUBSCRIBE"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1",
                        help="Headset IP address, e.g. 192.168.0.42")
    parser.add_argument("--port", type=int, default=5555,
                        help="UDP port on the headset (default 5555).")
    parser.add_argument("--print-every", type=float, default=1.0,
                        help="Seconds between status prints.")
    args = parser.parse_args()

    while True:
        try:
            run_once(args)
        except (ConnectionError, OSError) as exc:
            print(f"[subscriber] disconnected ({exc}); reconnecting in 1s…")
            time.sleep(1.0)
        except KeyboardInterrupt:
            break


def run_once(args) -> None:
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(0.1)
        print(f"[subscriber] registering with {args.host}:{args.port} over UDP")

        received = 0
        last_log = time.monotonic()
        last_registration = 0.0
        print_interval = max(0.1, args.print_every)

        while True:
            now = time.monotonic()
            if now - last_registration >= 1.0:
                sock.sendto(SUBSCRIBE_MESSAGE, (args.host, args.port))
                last_registration = now

            try:
                data, _remote = sock.recvfrom(65535)
            except socket.timeout:
                continue

            try:
                latest = json.loads(data.decode("utf-8").strip())
                received += 1
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                print(f"[subscriber] bad datagram: {exc}")
                continue

            now = time.monotonic()
            if now - last_log >= print_interval:
                rate = received / (now - last_log)
                received = 0
                last_log = now
                print(format_packet(latest, rate))


def format_packet(pkt: dict, rate: float) -> str:
    hands = by_side(pkt.get("hands"))
    ctrls = by_side(pkt.get("controllers"))

    header = (
        f"rate={rate:6.1f} Hz  "
        f"frame={pkt.get('frame', '?'):>7}  "
        f"hmd={fmt_xyz(hmd_position(pkt.get('hmd')))}"
    )
    hand_line = (
        f"  hands  L={hand_flag(hands.get('left'))} palm={fmt_xyz(pose_position(hands.get('left'), 'palm'))}"
        f"   R={hand_flag(hands.get('right'))} palm={fmt_xyz(pose_position(hands.get('right'), 'palm'))}"
    )
    ctrl_line = (
        f"  ctrls  L={controller_flag(ctrls.get('left'))}"
        f"   R={controller_flag(ctrls.get('right'))}"
    )
    return "\n".join((header, hand_line, ctrl_line))


def by_side(items) -> dict:
    """Turn a `hands`/`controllers` array into a {side: obj} map (ignores malformed)."""
    result: dict = {}
    if isinstance(items, list):
        for item in items:
            if isinstance(item, dict) and "side" in item:
                result[item["side"]] = item
    return result


def hmd_position(hmd: Optional[dict]) -> Optional[list]:
    if hmd is None or not hmd.get("tracked"):
        return None
    return hmd.get("position")


def hand_flag(hand: Optional[dict]) -> str:
    if hand is None or not hand.get("tracked"):
        return "off        "
    valid = sum(1 for lm in hand.get("landmarks", []) if lm.get("valid"))
    return f"ON({valid}/{LANDMARKS_PER_HAND})"


def pose_position(obj: Optional[dict], key: str) -> Optional[list]:
    """[x, y, z] of a { "valid", "position", "rotation" } sub-pose, or None."""
    if obj is None or not obj.get("tracked"):
        return None
    pose = obj.get(key)
    if pose is None or not pose.get("valid"):
        return None
    return pose.get("position")


def controller_flag(ctrl: Optional[dict]) -> str:
    """One-line controller summary: handle pose + analogs + whichever buttons are down."""
    if ctrl is None or not ctrl.get("tracked"):
        return "off"

    buttons = ctrl.get("buttons") or {}
    pressed = "".join(
        label for name, label in
        (("primary", "P"), ("secondary", "S"), ("menu", "M"), ("thumbstick", "T"))
        if buttons.get(name)
    )
    stick = ctrl.get("thumbstick") or [0.0, 0.0]
    return (
        f"ON pose={fmt_xyz(pose_position(ctrl, 'controller'))} "
        f"gripTrig={ctrl.get('grip_trigger', 0.0):.2f} "
        f"ctrlTrig={ctrl.get('ctrl_trigger', 0.0):.2f} "
        f"stick=({stick[0]:+.2f},{stick[1]:+.2f}) [{pressed or '-':4}]"
    )


def fmt_xyz(p: Optional[list]) -> str:
    if p is None:
        return "     (none)     "
    return f"({p[0]:+.3f}, {p[1]:+.3f}, {p[2]:+.3f})"


if __name__ == "__main__":
    main()
