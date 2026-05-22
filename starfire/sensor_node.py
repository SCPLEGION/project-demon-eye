"""
Starfire — Sensor Node (Realme C55 / Termux)
Streams GPS + accelerometer to ws://starfire.local:8765 every 200ms.

Requires:
    pkg install python termux-api
    pip install websockets

Run:
    python sensor_node.py
"""

from __future__ import annotations

import asyncio
import json
import shutil
import sys
import time
from pathlib import Path

try:
    import websockets
except ImportError:
    print("ERROR: websockets not installed.  pip install websockets")
    sys.exit(1)


CONFIG_PATH = Path(__file__).with_name("config.json")
RECONNECT_DELAY = 3.0
STREAM_INTERVAL = 0.2     # 200ms — 5Hz
SUBPROC_TIMEOUT = 2.0


def load_config() -> dict:
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return {
            "local_domain": "starfire.local",
            "local_port":   8765,
            "node_id_phone": "SENSOR_ALPHA",
        }


HAS_TERMUX = shutil.which("termux-location") is not None and shutil.which("termux-sensor") is not None
_warned_no_termux = False


async def _run(cmd: list[str], timeout: float = SUBPROC_TIMEOUT) -> dict | None:
    try:
        proc = await asyncio.create_subprocess_exec(
            *cmd,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.DEVNULL,
        )
        out, _ = await asyncio.wait_for(proc.communicate(), timeout=timeout)
        if proc.returncode != 0 or not out:
            return None
        return json.loads(out.decode("utf-8", errors="ignore"))
    except (asyncio.TimeoutError, FileNotFoundError, json.JSONDecodeError, Exception):
        return None


async def read_gps() -> dict:
    if not HAS_TERMUX:
        return {"lat": 0.0, "lon": 0.0, "altitude": 0.0,
                "bearing": 0.0, "accuracy": 0.0, "available": False}
    data = await _run(["termux-location", "-p", "gps", "-r", "last"])
    if not data:
        return {"lat": 0.0, "lon": 0.0, "altitude": 0.0,
                "bearing": 0.0, "accuracy": 0.0, "available": False}
    return {
        "lat":      float(data.get("latitude", 0.0)),
        "lon":      float(data.get("longitude", 0.0)),
        "altitude": float(data.get("altitude", 0.0)),
        "bearing":  float(data.get("bearing", 0.0)),
        "accuracy": float(data.get("accuracy", 0.0)),
        "available": True,
    }


async def read_accel() -> dict:
    if not HAS_TERMUX:
        return {"x": 0.0, "y": 0.0, "z": 0.0, "available": False}
    data = await _run(["termux-sensor", "-s", "accelerometer", "-n", "1"])
    if not data:
        return {"x": 0.0, "y": 0.0, "z": 0.0, "available": False}
    # termux-sensor returns {"<sensorName>": {"values": [x,y,z]}}
    try:
        for v in data.values():
            vals = v.get("values", [])
            if len(vals) >= 3:
                return {"x": float(vals[0]), "y": float(vals[1]),
                        "z": float(vals[2]), "available": True}
    except (AttributeError, KeyError, ValueError):
        pass
    return {"x": 0.0, "y": 0.0, "z": 0.0, "available": False}


async def stream(uri: str, node_id: str) -> None:
    """One connection lifetime — raises on disconnect."""
    print(f"SENSOR NODE ONLINE — streaming to {uri}")
    async with websockets.connect(uri, ping_interval=10, ping_timeout=20) as ws:
        # send identity first so discovery.py can fingerprint us
        await ws.send(json.dumps({
            "node_id":   node_id,
            "node_type": "phone",
            "mode":      "FULL",
            "status":    "LINK_ESTABLISHED",
            "timestamp": time.time(),
            "data":      {"hello": True},
        }, separators=(",", ":")))
        while True:
            gps, accel = await asyncio.gather(read_gps(), read_accel())
            packet = {
                "node_id":   node_id,
                "node_type": "phone",
                "mode":      "FULL",
                "status":    "LINK_ESTABLISHED",
                "timestamp": time.time(),
                "data": {
                    "gps":           gps,
                    "accelerometer": accel,
                },
            }
            try:
                await ws.send(json.dumps(packet, separators=(",", ":")))
            except websockets.ConnectionClosed:
                raise
            await asyncio.sleep(STREAM_INTERVAL)


async def main() -> None:
    cfg = load_config()
    host = cfg.get("local_domain", "starfire.local")
    port = int(cfg.get("local_port", 8765))
    node_id = cfg.get("node_id_phone", "SENSOR_ALPHA")
    uri = f"ws://{host}:{port}"
    global _warned_no_termux
    if not HAS_TERMUX and not _warned_no_termux:
        print("WARNING: termux-api not available — sending available:false sensor data")
        _warned_no_termux = True
    while True:
        try:
            await stream(uri, node_id)
        except KeyboardInterrupt:
            raise
        except Exception as e:
            print(f"SENSOR NODE RECONNECTING... ({type(e).__name__}: {e})")
            await asyncio.sleep(RECONNECT_DELAY)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nSENSOR NODE — SHUTDOWN")
