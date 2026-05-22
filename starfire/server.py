"""
Starfire — Cogitator Prime (PC ML broker)
YOLO + MiDaS + MediaPipe Face on DirectML (AMD RX 6600 XT, Windows).
Broadcasts aggregated results to all connected nodes at 30Hz.
"""

from __future__ import annotations

import asyncio
import io
import json
import logging
import sys
import threading
import time
from collections import deque
from http import HTTPStatus
from pathlib import Path

import numpy as np

try:
    import cv2
except ImportError:
    print("ERROR: opencv-python not installed.")
    sys.exit(1)

try:
    import onnxruntime as ort
except ImportError:
    print("ERROR: onnxruntime-directml not installed.")
    sys.exit(1)

try:
    import mediapipe as mp
    # On Python 3.13 pip resolves a stub package that lacks the `solutions`
    # namespace.  Probe it once at startup so face_thread can no-op cleanly
    # instead of crashing the thread.
    _ = getattr(mp, "solutions", None) or getattr(mp, "tasks", None)
    if _ is None:
        print("WARNING: mediapipe is installed but lacks both `solutions` and "
              "`tasks` namespaces (Python 3.13 stub?) — face detection disabled.")
        mp = None
except ImportError:
    print("WARNING: mediapipe not installed — face detection disabled.")
    mp = None

try:
    import websockets
except ImportError:
    print("ERROR: websockets not installed.")
    sys.exit(1)

import discovery as _discovery


CONFIG_PATH = Path(__file__).with_name("config.json")
MODELS_DIR  = Path(__file__).parent

# COCO 80 → Astartes terms
WEAPON_CLASSES = {"knife", "scissors", "baseball bat"}
COCO_NAMES = [
    "person","bicycle","car","motorcycle","airplane","bus","train","truck","boat",
    "traffic light","fire hydrant","stop sign","parking meter","bench","bird","cat",
    "dog","horse","sheep","cow","elephant","bear","zebra","giraffe","backpack",
    "umbrella","handbag","tie","suitcase","frisbee","skis","snowboard","sports ball",
    "kite","baseball bat","baseball glove","skateboard","surfboard","tennis racket",
    "bottle","wine glass","cup","fork","knife","spoon","bowl","banana","apple",
    "sandwich","orange","broccoli","carrot","hot dog","pizza","donut","cake","chair",
    "couch","potted plant","bed","dining table","toilet","tv","laptop","mouse",
    "remote","keyboard","cell phone","microwave","oven","toaster","sink","refrigerator",
    "book","clock","vase","scissors","teddy bear","hair drier","toothbrush",
]


def astartes_class(coco: str) -> str:
    if coco == "person":
        return "HOSTILE DETECTED"
    if coco in ("car", "truck", "bus", "motorcycle"):
        return "VEHICLE"
    if coco in WEAPON_CLASSES:
        return "WEAPON DETECTED"
    if coco in ("dog", "cat", "horse", "sheep", "cow", "bear", "bird"):
        return "FAUNA"
    return coco.upper()


def load_config() -> dict:
    with open(CONFIG_PATH, "r", encoding="utf-8") as fh:
        return json.load(fh)


def make_session(model_path: Path) -> ort.InferenceSession:
    sess_options = ort.SessionOptions()
    sess_options.execution_mode = ort.ExecutionMode.ORT_PARALLEL
    sess_options.inter_op_num_threads = 6
    sess_options.intra_op_num_threads = 6
    sess_options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    available = ort.get_available_providers()
    providers: list = []
    if "DmlExecutionProvider" in available:
        providers.append(("DmlExecutionProvider", {"device_id": 0}))
    providers.append("CPUExecutionProvider")
    return ort.InferenceSession(str(model_path), sess_options=sess_options, providers=providers)


def probe_directml() -> None:
    """Print a single, actionable warning if DirectML isn't loaded."""
    available = ort.get_available_providers()
    if "DmlExecutionProvider" in available:
        return
    print("")
    print("=" * 64)
    print("WARNING: DmlExecutionProvider not loaded — all ML on CPU.")
    print(f"         Providers available: {available}")
    print("         To fix:")
    print("           pip uninstall -y onnxruntime onnxruntime-azure onnxruntime-gpu")
    print("           pip install onnxruntime-directml")
    print("=" * 64)
    print("")


# ──────────────────────────────────────────────────────────────────────
# Shared frame buffers (deque maxlen=1 — always-latest)
# ──────────────────────────────────────────────────────────────────────
class FrameBus:
    def __init__(self) -> None:
        self.raw = deque(maxlen=1)         # BGR frames straight from camera
        self.lock = threading.Lock()
        self.cam_fps = 0.0


# ──────────────────────────────────────────────────────────────────────
# Camera capture thread
# ──────────────────────────────────────────────────────────────────────
def camera_thread(cfg: dict, bus: FrameBus, stop: threading.Event) -> None:
    url_template = "http://{ip}:{port}/video"
    while not stop.is_set():
        ip = cfg["phone_stream_ip"]
        port = cfg["phone_stream_port"]
        url = url_template.format(ip=ip, port=port)
        print(f"CAMERA: connecting to {url}")
        cap = cv2.VideoCapture(url)
        cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        cap.set(cv2.CAP_PROP_FPS, 30)
        if not cap.isOpened():
            print(f"CAMERA: open failed — retry in 2s")
            time.sleep(2.0)
            continue
        last = time.time()
        frames = 0
        while not stop.is_set():
            ok, frame = cap.read()
            if not ok or frame is None:
                print("CAMERA: read failed — reconnect")
                break
            bus.raw.append(frame)
            frames += 1
            now = time.time()
            if now - last >= 1.0:
                bus.cam_fps = frames / (now - last)
                frames = 0
                last = now
        cap.release()
        time.sleep(2.0)


# ──────────────────────────────────────────────────────────────────────
# YOLO thread (DirectML)
# ──────────────────────────────────────────────────────────────────────
class YoloOut:
    detections: list = []
    fps: float = 0.0


def _nms_numpy(boxes: np.ndarray, scores: np.ndarray, iou_thr: float = 0.45) -> list[int]:
    if boxes.size == 0:
        return []
    x1, y1, x2, y2 = boxes[:, 0], boxes[:, 1], boxes[:, 2], boxes[:, 3]
    areas = (x2 - x1) * (y2 - y1)
    order = scores.argsort()[::-1]
    keep: list[int] = []
    while order.size > 0:
        i = int(order[0])
        keep.append(i)
        if order.size == 1:
            break
        xx1 = np.maximum(x1[i], x1[order[1:]])
        yy1 = np.maximum(y1[i], y1[order[1:]])
        xx2 = np.minimum(x2[i], x2[order[1:]])
        yy2 = np.minimum(y2[i], y2[order[1:]])
        inter = np.maximum(0.0, xx2 - xx1) * np.maximum(0.0, yy2 - yy1)
        iou = inter / (areas[i] + areas[order[1:]] - inter + 1e-9)
        order = order[1:][iou <= iou_thr]
    return keep


def yolo_thread(cfg: dict, bus: FrameBus, out: YoloOut, stop: threading.Event) -> None:
    model_path = MODELS_DIR / cfg.get("yolo_model", "yolov8l.onnx")
    if not model_path.exists():
        print(f"WARNING: {model_path.name} missing — YOLO disabled")
        return
    try:
        sess = make_session(model_path)
    except Exception as e:
        print(f"WARNING: YOLO session init failed: {e}")
        return
    input_name = sess.get_inputs()[0].name
    size = int(cfg.get("yolo_input_size", 640))
    fps_limit = float(cfg.get("yolo_fps_limit", 30))
    min_dt = 1.0 / fps_limit
    last = time.time()
    frames = 0
    fps_t = time.time()
    while not stop.is_set():
        if not bus.raw:
            time.sleep(0.01)
            continue
        now = time.time()
        if now - last < min_dt:
            time.sleep(0.005)
            continue
        last = now
        frame = bus.raw[-1].copy()
        h0, w0 = frame.shape[:2]
        # letterbox to size x size
        scale = min(size / w0, size / h0)
        nw, nh = int(w0 * scale), int(h0 * scale)
        img = cv2.resize(frame, (nw, nh))
        canvas = np.full((size, size, 3), 114, dtype=np.uint8)
        canvas[:nh, :nw] = img
        x = canvas[:, :, ::-1].transpose(2, 0, 1)[None].astype(np.float32) / 255.0
        try:
            outputs = sess.run(None, {input_name: x})
        except Exception as e:
            print(f"YOLO: inference error {e}")
            continue
        pred = outputs[0]
        # ultralytics v8 ONNX export: (1, 84, N).  Transpose → (N, 84)
        if pred.ndim == 3 and pred.shape[1] in (84, 85):
            pred = pred[0].T
        elif pred.ndim == 3:
            pred = pred[0]
        if pred.shape[1] >= 85:
            obj = pred[:, 4]
            cls_scores = pred[:, 5:]
            cls = cls_scores.argmax(1)
            conf = obj * cls_scores[np.arange(len(cls)), cls]
        else:
            cls_scores = pred[:, 4:]
            cls = cls_scores.argmax(1)
            conf = cls_scores[np.arange(len(cls)), cls]
        mask = conf > 0.35
        if not mask.any():
            out.detections = []
        else:
            boxes_xywh = pred[mask, :4]
            conf = conf[mask]
            cls = cls[mask]
            # xywh → xyxy in letterbox space
            xy = boxes_xywh[:, :2]
            wh = boxes_xywh[:, 2:]
            x1y1 = xy - wh / 2
            x2y2 = xy + wh / 2
            boxes = np.concatenate([x1y1, x2y2], axis=1)
            # back to original frame coords
            boxes /= scale
            keep = _nms_numpy(boxes, conf)
            dets = []
            for idx in keep[:25]:
                ci = int(cls[idx])
                if ci < 0 or ci >= len(COCO_NAMES):
                    continue
                coco = COCO_NAMES[ci]
                dets.append({
                    "class":      astartes_class(coco),
                    "confidence": float(round(conf[idx], 3)),
                    "bbox":       [float(b) for b in boxes[idx]],
                })
            out.detections = dets
        frames += 1
        if now - fps_t >= 1.0:
            out.fps = frames / (now - fps_t)
            frames = 0
            fps_t = now


# ──────────────────────────────────────────────────────────────────────
# MiDaS depth thread
# ──────────────────────────────────────────────────────────────────────
class DepthOut:
    closest: float = 0.0
    map_png: bytes = b""
    fps: float = 0.0


def depth_thread(cfg: dict, bus: FrameBus, out: DepthOut, stop: threading.Event) -> None:
    model_path = MODELS_DIR / cfg.get("depth_model", "midas_v21_small.onnx")
    if not model_path.exists():
        print(f"WARNING: {model_path.name} missing — depth disabled")
        return
    try:
        sess = make_session(model_path)
    except Exception as e:
        print(f"WARNING: depth session init failed: {e}")
        return
    input_name = sess.get_inputs()[0].name
    in_shape = sess.get_inputs()[0].shape
    # MiDaS small wants 256x256
    h_in = int(in_shape[2]) if isinstance(in_shape[2], int) else 256
    w_in = int(in_shape[3]) if isinstance(in_shape[3], int) else 256
    fps_limit = float(cfg.get("depth_fps_limit", 15))
    min_dt = 1.0 / fps_limit
    last = time.time()
    frames = 0
    fps_t = time.time()
    mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
    std  = np.array([0.229, 0.224, 0.225], dtype=np.float32)
    while not stop.is_set():
        if not bus.raw:
            time.sleep(0.01)
            continue
        now = time.time()
        if now - last < min_dt:
            time.sleep(0.005)
            continue
        last = now
        frame = bus.raw[-1].copy()
        img = cv2.resize(frame, (w_in, h_in))
        rgb = img[:, :, ::-1].astype(np.float32) / 255.0
        rgb = (rgb - mean) / std
        x = rgb.transpose(2, 0, 1)[None]
        try:
            depth = sess.run(None, {input_name: x})[0]
        except Exception as e:
            print(f"DEPTH: inference error {e}")
            continue
        d = np.squeeze(depth)
        d_min, d_max = float(d.min()), float(d.max())
        if d_max > d_min:
            norm = (d - d_min) / (d_max - d_min)
        else:
            norm = np.zeros_like(d)
        out.closest = float(np.percentile(norm, 95))
        # downscale to 160x120, encode as PNG
        small = cv2.resize((norm * 255.0).astype(np.uint8), (160, 120))
        ok, buf = cv2.imencode(".png", small)
        if ok:
            out.map_png = buf.tobytes()
        frames += 1
        if now - fps_t >= 1.0:
            out.fps = frames / (now - fps_t)
            frames = 0
            fps_t = now


# ──────────────────────────────────────────────────────────────────────
# MediaPipe Face thread (CPU)
# ──────────────────────────────────────────────────────────────────────
class FaceOut:
    faces: list = []
    fps: float = 0.0


def _make_face_engine():
    """Return (process_fn, close_fn) for whichever mediapipe API is available,
    or None if neither works.  `process_fn(rgb_image)` returns a list of
    landmark-point lists (each list yields (x,y) tuples normalized 0..1)."""
    if mp is None:
        return None
    # ─── Modern Tasks API (mediapipe >= 0.10.x with stable Tasks namespace)
    try:
        import urllib.request
        from mediapipe.tasks.python import BaseOptions
        from mediapipe.tasks.python.vision import (
            FaceLandmarker, FaceLandmarkerOptions, RunningMode,
        )
        from mediapipe import Image as MpImage, ImageFormat as MpImageFormat

        model_path = Path(__file__).with_name("face_landmarker.task")
        if not model_path.exists():
            url = ("https://storage.googleapis.com/mediapipe-models/face_landmarker/"
                   "face_landmarker/float16/latest/face_landmarker.task")
            try:
                urllib.request.urlretrieve(url, model_path)
            except Exception as e:
                raise RuntimeError(f"could not fetch face_landmarker.task: {e}")
        opts = FaceLandmarkerOptions(
            base_options=BaseOptions(model_asset_path=str(model_path)),
            running_mode=RunningMode.IMAGE,
            num_faces=4,
        )
        landmarker = FaceLandmarker.create_from_options(opts)

        def _process(rgb):
            img = MpImage(image_format=MpImageFormat.SRGB, data=rgb)
            res = landmarker.detect(img)
            out_faces = []
            for lms in (res.face_landmarks or []):
                out_faces.append([(p.x, p.y) for p in lms])
            return out_faces

        def _close():
            try: landmarker.close()
            except Exception: pass
        print("FACE: using mediapipe.tasks FaceLandmarker")
        return _process, _close
    except Exception as e:
        print(f"FACE: Tasks API unavailable ({type(e).__name__}: {e}) — trying legacy solutions")

    # ─── Legacy solutions API
    try:
        face_mesh_mod = mp.solutions.face_mesh
        mp_face = face_mesh_mod.FaceMesh(
            static_image_mode=False, max_num_faces=4,
            refine_landmarks=False, min_detection_confidence=0.5)

        def _process(rgb):
            res = mp_face.process(rgb)
            out_faces = []
            for lms in (res.multi_face_landmarks or []):
                out_faces.append([(p.x, p.y) for p in lms.landmark])
            return out_faces

        def _close():
            try: mp_face.close()
            except Exception: pass
        print("FACE: using legacy mp.solutions.face_mesh")
        return _process, _close
    except Exception as e:
        print(f"FACE: legacy solutions also unavailable ({type(e).__name__}: {e}) — face thread disabled")
        return None


def face_thread(cfg: dict, bus: FrameBus, out: FaceOut, stop: threading.Event) -> None:
    engine = _make_face_engine()
    if engine is None:
        return
    process_fn, close_fn = engine

    fps_limit = float(cfg.get("face_fps_limit", 30))
    min_dt = 1.0 / fps_limit
    last = time.time()
    frames = 0
    fps_t = time.time()
    try:
        while not stop.is_set():
            if not bus.raw:
                time.sleep(0.01)
                continue
            now = time.time()
            if now - last < min_dt:
                time.sleep(0.005)
                continue
            last = now
            frame = bus.raw[-1].copy()
            small = cv2.resize(frame, (320, 240))
            rgb = cv2.cvtColor(small, cv2.COLOR_BGR2RGB)
            try:
                landmark_sets = process_fn(rgb)
            except Exception as e:
                print(f"FACE: process error {e}")
                continue
            faces = []
            for pts in landmark_sets:
                if len(pts) < 160:
                    continue
                # FaceMesh indices: upper lip 13, lower lip 14; left-brow inner 105, eye outer 33
                mouth_open = abs(pts[13][1] - pts[14][1]) > 0.04
                brow_raised = (pts[105][1] - pts[33][1]) > 0.10
                eye_squint  = abs(pts[159][1] - pts[145][1]) < 0.012
                cx = pts[1][0]
                if cx < 0.4:    pos = "left"
                elif cx > 0.6:  pos = "right"
                else:           pos = "center"
                faces.append({
                    "mouth_open":  bool(mouth_open),
                    "brow_raised": bool(brow_raised),
                    "eye_squint":  bool(eye_squint),
                    "position":    pos,
                })
            out.faces = faces
            frames += 1
            if now - fps_t >= 1.0:
                out.fps = frames / (now - fps_t)
                frames = 0
                fps_t = now
    finally:
        close_fn()


# ──────────────────────────────────────────────────────────────────────
# WebSocket broker / aggregator
# ──────────────────────────────────────────────────────────────────────
class Hub:
    def __init__(self, cfg: dict) -> None:
        self.cfg = cfg
        self.clients: dict[str, set] = {}   # node_id → {ws}
        self.lock = asyncio.Lock()
        self.last_payload: str = ""
        self.network_map: list = []
        self.node_id = cfg.get("node_id_pc", "PC_BROKER")

    async def add(self, ws, node_id: str) -> None:
        async with self.lock:
            self.clients.setdefault(node_id, set()).add(ws)

    async def remove(self, ws, node_id: str) -> None:
        async with self.lock:
            s = self.clients.get(node_id)
            if s and ws in s:
                s.remove(ws)
                if not s:
                    self.clients.pop(node_id, None)

    async def broadcast(self, payload: str | bytes, exclude=None) -> None:
        dead: list = []
        async with self.lock:
            all_ws: list = []
            for s in self.clients.values():
                all_ws.extend(s)
        for ws in all_ws:
            if ws is exclude:
                continue
            try:
                await ws.send(payload)
            except Exception:
                dead.append(ws)
        if dead:
            async with self.lock:
                for s in self.clients.values():
                    for ws in dead:
                        s.discard(ws)


async def handler(ws, hub: Hub) -> None:
    node_id = "UNKNOWN"
    try:
        # send identity first so scanner fingerprints us
        await ws.send(json.dumps({
            "node_id":   hub.node_id,
            "node_type": "pc",
            "mode":      "FULL",
            "status":    "LINK_ESTABLISHED",
            "timestamp": time.time(),
            "data":      {"hello": True},
        }, separators=(",", ":")))
        async for msg in ws:
            if isinstance(msg, bytes):
                # binary frames are forwarded unchanged
                await hub.broadcast(msg, exclude=ws)
                continue
            try:
                pkt = json.loads(msg)
                nid = pkt.get("node_id", "UNKNOWN")
                ntype = pkt.get("node_type", "?")
                if node_id == "UNKNOWN":
                    node_id = nid
                    role = {"quest":"HEAD OF ASTARTES","relay":"BACKPACK RELAY",
                            "phone":"ASTARTES SENSOR"}.get(ntype, "UNKNOWN")
                    print(f"NAV LINK ESTABLISHED: {node_id} [{role}]")
                    await hub.add(ws, node_id)
            except json.JSONDecodeError:
                continue
            # forward to all other nodes
            await hub.broadcast(msg, exclude=ws)
    except Exception:
        pass
    finally:
        if node_id != "UNKNOWN":
            print(f"NAV LINK LOST: {node_id}")
            await hub.remove(ws, node_id)


async def aggregator(hub: Hub, bus: FrameBus,
                     yo: YoloOut, do: DepthOut, fo: FaceOut,
                     stop: asyncio.Event) -> None:
    hz = float(hub.cfg.get("broadcast_hz", 30))
    dt = 1.0 / hz
    while not stop.is_set():
        packet = {
            "node_id":   hub.node_id,
            "node_type": "pc",
            "mode":      "FULL",
            "status":    "ACTIVE",
            "timestamp": time.time(),
            "data": {
                "detections": yo.detections,
                "depth":      {"closest": do.closest},
                "faces":      fo.faces,
                "network_map": hub.network_map,
                "pipeline_fps": {
                    "yolo":   round(yo.fps, 1),
                    "depth":  round(do.fps, 1),
                    "face":   round(fo.fps, 1),
                    "camera": round(bus.cam_fps, 1),
                },
            },
        }
        payload = json.dumps(packet, separators=(",", ":"))
        if payload != hub.last_payload:
            hub.last_payload = payload
            await hub.broadcast(payload)
        if do.map_png:
            await hub.broadcast(do.map_png)
        await asyncio.sleep(dt)


async def stats_printer(hub: Hub, bus: FrameBus,
                        yo: YoloOut, do: DepthOut, fo: FaceOut,
                        stop: asyncio.Event) -> None:
    while not stop.is_set():
        await asyncio.sleep(5.0)
        nodes = " ● ".join(sorted(hub.clients.keys())) or "(none)"
        print(f"[YOLO] {yo.fps:5.1f} FPS | [DEPTH] {do.fps:5.1f} FPS | "
              f"[FACE] {fo.fps:5.1f} FPS | [CAM] {bus.cam_fps:5.1f} FPS")
        print(f"[NODES] {nodes}")


async def discovery_loop(hub: Hub, cfg: dict, stop: asyncio.Event) -> None:
    interval = float(cfg.get("scan_interval_seconds", 60))
    while not stop.is_set():
        try:
            result = await _discovery.scan_once(quiet=True)
            hub.network_map = result.get("nodes", [])
            for n in hub.network_map:
                if n.get("node_type") == "phone":
                    new_ip = n["ip"]
                    if cfg["phone_stream_ip"] != new_ip:
                        cfg["phone_stream_ip"] = new_ip
                        print(f"AUTO-CONFIGURED: camera stream → {new_ip}:{cfg['phone_stream_port']}")
        except Exception as e:
            print(f"WARNING: discovery scan failed: {e}")
        try:
            await asyncio.wait_for(stop.wait(), timeout=interval)
        except asyncio.TimeoutError:
            pass


class _DropInvalidHandshake(logging.Filter):
    """Suppress noisy tracebacks for stray TCP connections that never finish a
    WebSocket handshake (port scanners, antivirus probes, etc.)."""
    def filter(self, record: logging.LogRecord) -> bool:
        try:
            msg = record.getMessage()
        except Exception:
            return True
        if "did not receive a valid HTTP request" in msg: return False
        if "opening handshake failed" in msg:             return False
        if "connection closed while reading HTTP request" in msg: return False
        return True


def _install_ws_log_filter() -> None:
    f = _DropInvalidHandshake()
    for name in ("websockets", "websockets.server", "websockets.asyncio.server"):
        lg = logging.getLogger(name)
        lg.addFilter(f)
        if lg.level == logging.NOTSET or lg.level < logging.WARNING:
            lg.setLevel(logging.WARNING)


async def _process_request(connection, request):
    """Return early with HTTP 400 for non-WS clients so they disconnect cleanly
    instead of leaving the server mid-handshake.  Returning None lets the
    upgrade proceed normally."""
    try:
        headers = getattr(request, "headers", {}) or {}
        upgrade = headers.get("Upgrade", "") or headers.get("upgrade", "")
        if "websocket" not in str(upgrade).lower():
            from websockets.http11 import Response
            return Response(HTTPStatus.BAD_REQUEST, "Bad Request",
                            headers={"Content-Type": "text/plain"},
                            body=b"This is the Starfire WebSocket broker.\n")
    except Exception:
        return None
    return None


async def main() -> None:
    cfg = load_config()
    print("╔═══════════════════════════════╗")
    print("║  STARFIRE COGITATOR PRIME     ║")
    print("║  MACHINE SPIRIT AWAKENING     ║")
    print("╚═══════════════════════════════╝")
    print(f"ONNX providers available: {ort.get_available_providers()}")
    probe_directml()
    _install_ws_log_filter()

    bus = FrameBus()
    yo  = YoloOut()
    do  = DepthOut()
    fo  = FaceOut()
    stop_threads = threading.Event()

    threads = [
        threading.Thread(target=camera_thread, args=(cfg, bus, stop_threads), daemon=True, name="camera"),
        threading.Thread(target=yolo_thread,   args=(cfg, bus, yo, stop_threads), daemon=True, name="yolo"),
        threading.Thread(target=depth_thread,  args=(cfg, bus, do, stop_threads), daemon=True, name="depth"),
        threading.Thread(target=face_thread,   args=(cfg, bus, fo, stop_threads), daemon=True, name="face"),
    ]
    for t in threads:
        t.start()

    hub = Hub(cfg)
    stop_async = asyncio.Event()

    async def _handler(ws):
        await handler(ws, hub)

    port = int(cfg.get("pc_port", 8765))
    server = await websockets.serve(_handler, "0.0.0.0", port, max_size=None,
                                    ping_interval=20, ping_timeout=20,
                                    process_request=_process_request)
    print(f"BROKER LISTENING: 0.0.0.0:{port}")

    tasks = [
        asyncio.create_task(aggregator(hub, bus, yo, do, fo, stop_async)),
        asyncio.create_task(stats_printer(hub, bus, yo, do, fo, stop_async)),
        asyncio.create_task(discovery_loop(hub, cfg, stop_async)),
    ]
    try:
        await asyncio.Future()
    except (KeyboardInterrupt, asyncio.CancelledError):
        pass
    finally:
        stop_async.set()
        stop_threads.set()
        for t in tasks:
            t.cancel()
        server.close()
        await server.wait_closed()
        print("COGITATOR PRIME — SHUTDOWN")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nCOGITATOR PRIME — interrupted")
