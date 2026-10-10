"""Meta Quest teleoperation leader using the current UDP pose publisher.

The class deliberately follows ``So101Wist``'s small interface:
``__init__``, ``connect``, ``disconnect``, ``read_command_tcp`` and ``_run``.
``connect`` starts one or more video workers and one Quest pose worker; ``_run``
is the single worker implementation for both paths. The ``read_command_tcp``
name is kept for the ``So101Wist`` interface; pose data itself is received over UDP.
"""

from __future__ import annotations

import json
import logging
import shutil
import socket
import subprocess
import threading
import time
from dataclasses import dataclass
from typing import Any

import numpy as np

from organza_robot.leader.leader_base import LeaderBase, LeaderConfigBase


LOGGER = logging.getLogger(__name__)


@dataclass
class VideoStreamConfig:
    """Configuration for one camera-to-Quest RTP stream."""

    device: str
    port: int


@dataclass
class VrConfig(LeaderConfigBase):
    """Video streams, Quest UDP pose subscription, and worker settings."""

    video_streams: tuple[VideoStreamConfig, ...]
    width: int = 1280
    height: int = 720
    fps: int = 30
    bitrate: int = 3000
    io_mode: int = 2
    key_int_max: int = 30

    host: str = "192.168.0.207"
    pose_port: int = 5555
    pose_source: str = "controller"  # controller, palm, or hmd
    pose_timeout_s: float = 0.25       # 0 disables stale-pose rejection

    video_restart_delay_s: float = 2.0
    max_video_restarts: int = -1       # -1 means unlimited, 0 disables retry
    pose_register_interval_s: float = 1.0
    udp_retry_delay_s: float = 1.0
    verbose: bool = False


class Vr(LeaderBase):
    """Quest pose leader with an integrated low-latency camera sender."""

    def __init__(self, config: VrConfig):
        super().__init__(config)

        self._stop_event = threading.Event()
        self._state_lock = threading.Lock()
        self._pose_thread: threading.Thread | None = None
        self._video_threads: list[threading.Thread] = []
        self._pose_socket: socket.socket | None = None
        self._video_processes: dict[str, subprocess.Popen[bytes]] = {}
        self._latest_packet: dict[str, Any] | None = None
        self._latest_packet_time: float | None = None
        self._connected = False

    def connect(self) -> None:
        """Start video and Quest workers."""

        if self._connected:
            return

        config = self.config
        video_streams = config.video_streams
        if not video_streams:
            raise ValueError("at least one video stream must be configured")
        if shutil.which("gst-launch-1.0") is None:
            raise FileNotFoundError("gst-launch-1.0 was not found in PATH")

        config.pose_source = str(config.pose_source).lower()
        if config.pose_source not in {"controller", "palm", "hmd"}:
            raise ValueError("pose_source must be 'controller', 'palm', or 'hmd'")

        self._stop_event.clear()
        with self._state_lock:
            self._latest_packet = None
            self._latest_packet_time = None
        self._connected = True

        try:
            self._video_threads = []
            for index, stream in enumerate(video_streams):
                video_thread = threading.Thread(
                    target=self._run,
                    args=("video", stream.device, int(stream.port)),
                    name=f"vr-video-{index}",
                    daemon=True,
                )
                self._video_threads.append(video_thread)
            self._pose_thread = threading.Thread(
                target=self._run,
                args=("pose",),
                name="vr-pose",
                daemon=True,
            )
            for video_thread in self._video_threads:
                video_thread.start()
            self._pose_thread.start()
            LOGGER.info(
                "VR connected: %d video stream(s), pose_udp=%s:%d (%s)",
                len(video_streams),
                config.host,
                config.pose_port,
                config.pose_source,
            )
        except BaseException:
            self.disconnect()
            raise

    def disconnect(self) -> None:
        """Stop workers, close the Quest UDP socket, and terminate GStreamer."""

        self._stop_event.set()
        with self._state_lock:
            pose_socket = self._pose_socket
            video_processes = list(self._video_processes.values())
            self._pose_socket = None
            self._video_processes.clear()
            self._latest_packet = None
            self._latest_packet_time = None

        if pose_socket is not None:
            try:
                pose_socket.close()
            except OSError:
                pass

        for video_process in video_processes:
            if video_process.poll() is None:
                video_process.terminate()
                try:
                    video_process.wait(timeout=5.0)
                except subprocess.TimeoutExpired:
                    LOGGER.warning("GStreamer did not exit; killing it")
                    video_process.kill()
                    video_process.wait()

        current = threading.current_thread()
        for thread in (*self._video_threads, self._pose_thread):
            if thread is not None and thread is not current:
                thread.join(timeout=5.0)
        self._pose_thread = None
        self._video_threads = []
        self._connected = False
        LOGGER.info("VR disconnected")

    def read_command_tcp(
        self,
    ) -> dict[str, tuple[np.ndarray, np.ndarray] | None] | None:
        """Return left/right tracked poses, or ``None`` when neither is available.

        With ``pose_source="controller"`` (the default), values come from both
        controllers. ``pose_source="palm"`` returns both palm poses; ``"hmd"``
        returns a single ``{"hmd": pose}`` entry. The method name is retained
        for compatibility with ``LeaderBase``; the pose packets arrive over UDP.
        """

        with self._state_lock:
            packet = self._latest_packet
            packet_time = self._latest_packet_time
        if packet is None or packet_time is None:
            return None
        if self.config.pose_timeout_s > 0:
            if time.monotonic() - packet_time > self.config.pose_timeout_s:
                return None

        source = str(self.config.pose_source).lower()

        def parse_pose(pose: Any, require_valid: bool = True) -> tuple[np.ndarray, np.ndarray] | None:
            if not isinstance(pose, dict):
                return None
            if require_valid and pose.get("valid") is not True:
                return None
            try:
                xyz = np.asarray(pose.get("position"), dtype=np.float64).reshape(-1)
                quat = np.asarray(pose.get("rotation"), dtype=np.float64).reshape(-1)
            except (TypeError, ValueError):
                return None
            if xyz.shape != (3,) or quat.shape != (4,):
                return None
            if not np.isfinite(xyz).all() or not np.isfinite(quat).all():
                return None
            norm = float(np.linalg.norm(quat))
            if norm <= np.finfo(np.float64).eps:
                return None
            return xyz.copy(), (quat / norm).copy()

        if source == "hmd":
            hmd = packet.get("hmd")
            if not isinstance(hmd, dict) or hmd.get("tracked") is not True:
                return None
            pose = parse_pose(hmd, require_valid=False)
            return {"hmd": pose} if pose is not None else None

        collection_name, pose_key = (
            ("controllers", "controller") if source == "controller" else ("hands", "palm")
        )
        collection = packet.get(collection_name)
        if not isinstance(collection, list):
            return None

        poses: dict[str, tuple[np.ndarray, np.ndarray] | None] = {
            "left": None,
            "right": None,
        }
        for item in collection:
            if not isinstance(item, dict):
                continue
            side = item.get("side")
            if side not in poses or item.get("tracked") is not True:
                continue
            poses[side] = parse_pose(item.get(pose_key))

        return poses if any(pose is not None for pose in poses.values()) else None

    def read_command_end(self) -> dict[str, dict[str, Any] | None] | None:
        """Return left/right controller button values, or ``None`` if unavailable."""

        with self._state_lock:
            packet = self._latest_packet
            packet_time = self._latest_packet_time
        if packet is None or packet_time is None:
            return None
        if self.config.pose_timeout_s > 0:
            if time.monotonic() - packet_time > self.config.pose_timeout_s:
                return None

        controllers = packet.get("controllers")
        if not isinstance(controllers, list):
            return None

        buttons_by_side: dict[str, dict[str, Any] | None] = {
            "left": None,
            "right": None,
        }
        for item in controllers:
            if not isinstance(item, dict):
                continue
            side = item.get("side")
            if side not in buttons_by_side or item.get("tracked") is not True:
                continue
            buttons = item.get("buttons")
            if not isinstance(buttons, dict):
                controller = item.get("controller")
                buttons = controller.get("buttons") if isinstance(controller, dict) else None
            if isinstance(buttons, dict):
                buttons_by_side[side] = buttons.copy()

        return (
            buttons_by_side
            if any(buttons is not None for buttons in buttons_by_side.values())
            else None
        )

    def _run(self, worker: str, device: str | None = None, stream_port: int | None = None) -> None:
        """Run either a video sender or the Quest pose subscriber."""

        if worker == "video":
            if device is None or stream_port is None:
                raise ValueError("video worker requires a device and port")
            stream_key = f"{device}:{stream_port}"
            restarts = 0
            while not self._stop_event.is_set():
                process: subprocess.Popen[bytes] | None = None
                try:
                    config = self.config
                    pipeline: list[str | None] = [
                        "gst-launch-1.0",
                        "-e",
                        "-v" if config.verbose else None,
                        "v4l2src",
                        f"device={device}",
                        f"io-mode={config.io_mode}",
                        "!",
                        f"image/jpeg,width={config.width},height={config.height},framerate={config.fps}/1",
                        "!",
                        "jpegdec",
                        "!",
                        "videoconvert",
                        "!",
                        "video/x-raw,format=I420",
                        "!",
                        "queue",
                        "max-size-buffers=1",
                        "leaky=downstream",
                        "!",
                        "x264enc",
                        "tune=zerolatency",
                        "byte-stream=true",
                        f"bitrate={config.bitrate}",
                        "speed-preset=ultrafast",
                        f"key-int-max={config.key_int_max}",
                        "!",
                        "rtph264pay",
                        "pt=96",
                        "config-interval=1",
                        "mtu=1200",
                        "!",
                        "udpsink",
                        f"host={config.host}",
                        f"port={stream_port}",
                        "sync=false",
                        "async=false",
                    ]
                    command = [item for item in pipeline if item is not None]
                    LOGGER.info("Starting Quest video pipeline (%s): %s", stream_key, " ".join(command))
                    process = subprocess.Popen(command)
                    with self._state_lock:
                        if self._stop_event.is_set():
                            process.terminate()
                        else:
                            self._video_processes[stream_key] = process

                    while not self._stop_event.is_set():
                        try:
                            return_code = process.wait(timeout=0.2)
                            break
                        except subprocess.TimeoutExpired:
                            continue
                    else:
                        if process.poll() is None:
                            process.terminate()
                        return

                    with self._state_lock:
                        if self._video_processes.get(stream_key) is process:
                            del self._video_processes[stream_key]
                    if return_code == 0 or self._stop_event.is_set():
                        return
                    if config.max_video_restarts >= 0 and restarts >= config.max_video_restarts:
                        LOGGER.error(
                            "Quest video pipeline (%s) exited with status %s; restart limit reached",
                            stream_key,
                            return_code,
                        )
                        return
                    restarts += 1
                    LOGGER.warning(
                        "Quest video pipeline (%s) exited with status %s; restarting (%d/%s) in %.1f seconds",
                        stream_key,
                        return_code,
                        restarts,
                        "unlimited" if config.max_video_restarts < 0 else config.max_video_restarts,
                        config.video_restart_delay_s,
                    )
                    if self._stop_event.wait(config.video_restart_delay_s):
                        return
                except OSError as exc:
                    if self._stop_event.is_set():
                        return
                    LOGGER.error("Could not start Quest video pipeline (%s): %s", stream_key, exc)
                    if config.max_video_restarts >= 0 and restarts >= config.max_video_restarts:
                        return
                    restarts += 1
                    if self._stop_event.wait(config.video_restart_delay_s):
                        return
                finally:
                    if process is not None:
                        with self._state_lock:
                            if self._video_processes.get(stream_key) is process:
                                del self._video_processes[stream_key]
            return

        while not self._stop_event.is_set():
            sock: socket.socket | None = None
            try:
                config = self.config
                LOGGER.info(
                    "Registering UDP pose subscriber with Quest %s:%d",
                    config.host,
                    config.pose_port,
                )
                sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                sock.settimeout(0.1)
                sock.connect((config.host, config.pose_port))
                with self._state_lock:
                    self._pose_socket = sock
                LOGGER.info(
                    "UDP pose subscriber ready; registering every %.1f seconds",
                    config.pose_register_interval_s,
                )

                next_registration = 0.0
                last_frame: int | None = None
                last_fresh_packet_time: float | None = None
                while not self._stop_event.is_set():
                    now = time.monotonic()
                    if now >= next_registration:
                        sock.send(b"META_POSE_SUBSCRIBE")
                        next_registration = now + max(0.1, config.pose_register_interval_s)

                    try:
                        payload = sock.recv(65535)
                    except socket.timeout:
                        continue
                    try:
                        packet = json.loads(payload.decode("utf-8").strip())
                    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                        LOGGER.warning("Ignoring malformed Quest pose datagram: %s", exc)
                        continue
                    if not isinstance(packet, dict):
                        LOGGER.warning("Ignoring non-object Quest pose datagram")
                        continue

                    frame_value = packet.get("frame")
                    packet_time = time.monotonic()
                    if type(frame_value) is int:
                        if last_frame is not None and frame_value < last_frame:
                            # Ignore reordered packets, but allow a new Unity app
                            # session to restart its frame counter after a pause.
                            restart_window = max(1.0, config.pose_timeout_s)
                            if (last_fresh_packet_time is None or
                                    packet_time - last_fresh_packet_time <= restart_window):
                                continue
                            LOGGER.info("Quest frame counter restarted; accepting the new session")
                            last_frame = None

                        if last_frame == frame_value:
                            # Unity sends at 60 Hz but samples on render frames;
                            # repeated snapshots do not refresh the pose timeout.
                            continue
                        last_frame = frame_value
                        last_fresh_packet_time = packet_time

                    with self._state_lock:
                        self._latest_packet = packet
                        self._latest_packet_time = packet_time
            except OSError as exc:
                if not self._stop_event.is_set():
                    LOGGER.warning(
                        "Quest UDP pose receiver failed (%s); retrying in %.1f seconds",
                        exc,
                        self.config.udp_retry_delay_s,
                    )
                    self._stop_event.wait(self.config.udp_retry_delay_s)
            finally:
                with self._state_lock:
                    if self._pose_socket is sock:
                        self._pose_socket = None
                if sock is not None:
                    try:
                        sock.close()
                    except OSError:
                        pass


# Compatibility aliases for existing imports from organza_robot.leader.
RebotWist = Vr
RebotWistConfig = VrConfig
