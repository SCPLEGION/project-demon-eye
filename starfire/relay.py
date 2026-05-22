"""
Starfire — Relay Node (laptop in backpack)
ZERO ML, ZERO OpenCV.  RAM target <20MB, CPU <2%.

- mDNS registers starfire._websocket._tcp.local on port 8765
- Listens on 0.0.0.0:8765
- Per client: opens upstream WS to PC; bidirectional pipe in FULL mode
- If PC unreachable: emit FALLBACK status to all clients at 1Hz
- Auto switch FULL <-> FALLBACK as PC reachability changes
- Background: discovery scan every scan_interval_seconds
"""

from __future__ import annotations

import asyncio
import json
import socket
import sys
import time
from pathlib import Path

try:
    import psutil
except ImportError:
    print("ERROR: psutil not installed.  pip install psutil")
    sys.exit(1)

try:
    import websockets
    from websockets.exceptions import ConnectionClosed
except ImportError:
    print("ERROR: websockets not installed.  pip install websockets")
    sys.exit(1)

try:
    from zeroconf import ServiceInfo, Zeroconf
except ImportError:
    print("ERROR: zeroconf not installed.  pip install zeroconf")
    sys.exit(1)

import discovery as _discovery


CONFIG_PATH = Path(__file__).with_name("config.json")


def load_config() -> dict:
    with open(CONFIG_PATH, "r", encoding="utf-8") as fh:
        return json.load(fh)


def list_active_ipv4() -> list[tuple[str, str]]:
    """[(interface_name, ip), ...]"""
    out: list[tuple[str, str]] = []
    for name, addrs in psutil.net_if_addrs().items():
        for a in addrs:
            if a.family == socket.AF_INET and not a.address.startswith("127."):
                out.append((name, a.address))
    return out


class RelayState:
    def __init__(self, cfg: dict) -> None:
        self.cfg = cfg
        self.pc_domain = cfg.get("pc_domain", "starfire.yourdomain.com")
        self.pc_port   = int(cfg.get("pc_port", 8765))
        self.mode = "FALLBACK"      # FULL when PC reachable
        self.clients: set = set()
        self.node_id = cfg.get("node_id_relay", "RELAY_NODE")
        self.dns_timeout = float(cfg.get("dns_timeout_seconds", 3))
        self.ping_interval = float(cfg.get("pc_ping_interval_seconds", 5))
        self.scan_interval = float(cfg.get("scan_interval_seconds", 60))
        self.interfaces = [ip for _, ip in list_active_ipv4()]

    def fallback_packet(self) -> str:
        return json.dumps({
            "node_id":   self.node_id,
            "node_type": "relay",
            "mode":      "FALLBACK",
            "status":    "COGITATOR_LOST",
            "timestamp": time.time(),
            "data": {
                "message":    "PC unreachable — local protocols active",
                "interfaces": self.interfaces,
            },
        }, separators=(",", ":"))

    def identity_packet(self) -> str:
        return json.dumps({
            "node_id":   self.node_id,
            "node_type": "relay",
            "mode":      self.mode,
            "status":    "LINK_ESTABLISHED",
            "timestamp": time.time(),
            "data":      {"interfaces": self.interfaces},
        }, separators=(",", ":"))


async def _pc_reachable(state: RelayState) -> bool:
    uri = f"ws://{state.pc_domain}:{state.pc_port}"
    try:
        # DNS resolution + WS handshake within dns_timeout
        async with websockets.connect(uri, open_timeout=state.dns_timeout,
                                      close_timeout=0.5, ping_interval=None) as ws:
            await ws.close()
        return True
    except Exception:
        return False


async def _broadcast(state: RelayState, payload: str) -> None:
    dead = []
    for ws in list(state.clients):
        try:
            await ws.send(payload)
        except Exception:
            dead.append(ws)
    for ws in dead:
        state.clients.discard(ws)


async def mode_watcher(state: RelayState) -> None:
    while True:
        ok = await _pc_reachable(state)
        new_mode = "FULL" if ok else "FALLBACK"
        if new_mode != state.mode:
            state.mode = new_mode
            if new_mode == "FULL":
                print("MODE: FULL — COGITATOR PRIME ONLINE")
                await _broadcast(state, json.dumps({
                    "node_id":   state.node_id,
                    "node_type": "relay",
                    "mode":      "FULL",
                    "status":    "COGITATOR_LINK_RESTORED",
                    "timestamp": time.time(),
                    "data":      {},
                }, separators=(",", ":")))
            else:
                print("MODE: FALLBACK — RUNNING LOCAL PROTOCOLS")
                await _broadcast(state, state.fallback_packet())
        await asyncio.sleep(state.ping_interval)


async def fallback_heartbeat(state: RelayState) -> None:
    """1Hz emit while in FALLBACK so HUDs keep status fresh."""
    while True:
        if state.mode == "FALLBACK" and state.clients:
            await _broadcast(state, state.fallback_packet())
        await asyncio.sleep(1.0)


async def _pipe(src, dst) -> None:
    try:
        async for msg in src:
            await dst.send(msg)
    except ConnectionClosed:
        return
    except Exception:
        return


async def handle_full(state: RelayState, client_ws) -> None:
    uri = f"ws://{state.pc_domain}:{state.pc_port}"
    try:
        async with websockets.connect(uri, open_timeout=state.dns_timeout,
                                      max_size=None, ping_interval=20) as pc_ws:
            await asyncio.gather(
                _pipe(client_ws, pc_ws),
                _pipe(pc_ws, client_ws),
            )
    except Exception as e:
        # PC dropped mid-session — notify client, fall back
        try:
            await client_ws.send(state.fallback_packet())
        except Exception:
            pass
        state.mode = "FALLBACK"
        print(f"FULL pipe lost: {type(e).__name__} — switching to FALLBACK")


async def handle_fallback(state: RelayState, client_ws) -> None:
    try:
        await client_ws.send(state.fallback_packet())
        # consume client traffic but do nothing with it
        async for _ in client_ws:
            pass
    except ConnectionClosed:
        return


async def client_handler(client_ws) -> None:
    state: RelayState = client_ws.relay_state
    state.clients.add(client_ws)
    peer = getattr(client_ws, "remote_address", ("?",))[0]
    print(f"CLIENT CONNECTED: {peer}  (mode={state.mode})")
    await client_ws.send(state.identity_packet())
    try:
        if state.mode == "FULL":
            await handle_full(state, client_ws)
        else:
            await handle_fallback(state, client_ws)
    finally:
        state.clients.discard(client_ws)
        print(f"CLIENT DISCONNECTED: {peer}")


def register_mdns(cfg: dict, ips: list[str]) -> tuple[Zeroconf, ServiceInfo]:
    zc = Zeroconf()
    hostname = socket.gethostname().split(".")[0]
    info = ServiceInfo(
        type_="_websocket._tcp.local.",
        name=f"starfire._websocket._tcp.local.",
        addresses=[socket.inet_aton(ip) for ip in ips if ":" not in ip],
        port=int(cfg.get("local_port", 8765)),
        properties={"node": "relay", "id": cfg.get("node_id_relay", "RELAY_NODE")},
        server=f"{hostname}.local.",
    )
    zc.register_service(info)
    print(f"mDNS REGISTERED: starfire._websocket._tcp.local. on port {cfg.get('local_port', 8765)}")
    return zc, info


async def discovery_loop(state: RelayState) -> None:
    while True:
        try:
            result = await _discovery.scan_once(quiet=True)
            for n in result.get("nodes", []):
                if n.get("node_type") == "pc":
                    new_domain = n["ip"]
                    if state.pc_domain != new_domain:
                        state.pc_domain = new_domain
                        print(f"AUTO-CONFIGURED: PC domain → {new_domain}")
                    break
        except Exception as e:
            print(f"WARNING: discovery scan failed: {e}")
        await asyncio.sleep(state.scan_interval)


async def main() -> None:
    cfg = load_config()
    state = RelayState(cfg)

    print("╔═══════════════════════════════╗")
    print("║  STARFIRE RELAY NODE          ║")
    print("║  ASTARTES BACKPACK COGITATOR  ║")
    print("╚═══════════════════════════════╝")
    for name, ip in list_active_ipv4():
        low = name.lower()
        if "wi-fi" in low or "wlan" in low:
            label = "Wi-Fi      "
        elif "hotspot" in low or low.startswith("ap"):
            label = "Hotspot    "
        elif "tether" in low or "rndis" in low or "usb" in low:
            label = "USB-Tether "
        else:
            label = name[:11].ljust(11)
        print(f"INTERFACE: {label} {ip}")

    zc, info = register_mdns(cfg, state.interfaces)

    # Initial discovery scan (prints map)
    try:
        await _discovery.scan_once()
    except Exception as e:
        print(f"WARNING: initial scan failed: {e}")

    # Probe PC once for initial mode
    state.mode = "FULL" if await _pc_reachable(state) else "FALLBACK"
    print(f"INITIAL MODE: {state.mode}")

    async def _handler(ws):
        ws.relay_state = state
        await client_handler(ws)

    port = int(cfg.get("local_port", 8765))
    server = await websockets.serve(_handler, "0.0.0.0", port, max_size=None,
                                    ping_interval=20, ping_timeout=20)
    print(f"LISTENING: 0.0.0.0:{port}")

    tasks = [
        asyncio.create_task(mode_watcher(state)),
        asyncio.create_task(fallback_heartbeat(state)),
        asyncio.create_task(discovery_loop(state)),
    ]
    try:
        await asyncio.Future()
    finally:
        for t in tasks:
            t.cancel()
        server.close()
        await server.wait_closed()
        try:
            zc.unregister_service(info)
            zc.close()
        except Exception:
            pass
        print("RELAY — SHUTDOWN")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nRELAY — interrupted")
