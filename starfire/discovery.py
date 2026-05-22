"""
Starfire — Astartes Network Scanner
Mechanicus Augur Array

Standalone CLI:
    python discovery.py
    python discovery.py --monitor
    python discovery.py --subnet 192.168.137.0/24

Library API:
    from discovery import scan_once, monitor_forever
    map_data = await scan_once()
    await monitor_forever(callback=on_change)
"""

from __future__ import annotations

import argparse
import asyncio
import ipaddress
import json
import os
import platform
import socket
import sys
import time
from dataclasses import dataclass, asdict
from datetime import datetime
from pathlib import Path
from typing import Awaitable, Callable, Iterable

try:
    import psutil
except ImportError:
    print("ERROR: psutil not installed.  pip install psutil")
    sys.exit(1)

try:
    import websockets
except ImportError:
    print("ERROR: websockets not installed.  pip install websockets")
    sys.exit(1)


CONFIG_PATH = Path(__file__).with_name("config.json")
MAP_PATH = Path(__file__).with_name("network_map.json")

ROLE_BY_TYPE = {
    "quest": "HEAD OF ASTARTES",
    "relay": "BACKPACK RELAY",
    "phone": "ASTARTES SENSOR",
    "pc":    "COGITATOR PRIME",
}

NODE_ID_DEFAULT = {
    "quest": "HUD_PRIME",
    "relay": "RELAY_NODE",
    "phone": "SENSOR_ALPHA",
    "pc":    "PC_BROKER",
}

PING_TIMEOUT_MS = 500
WS_TIMEOUT_S    = 0.5
SCAN_PORT       = 8765
IS_WINDOWS      = platform.system().lower().startswith("win")
PING_CONCURRENCY = 64   # cap simultaneous ping subprocesses


@dataclass
class NetworkNode:
    ip: str
    role: str
    node_id: str
    node_type: str
    status: str
    latency_ms: int
    interface: str


def load_config() -> dict:
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return {}


def _interface_label(addr: str, interfaces: dict[str, list[str]]) -> str:
    for name, ips in interfaces.items():
        if addr in ips:
            low = name.lower()
            if "wi-fi" in low or "wlan" in low or "wireless" in low:
                return "WiFi"
            if "hotspot" in low or "ap" in low:
                return "Hotspot"
            if "tether" in low or "rndis" in low or "usb" in low:
                return "USB Tether"
            return name
    return "?"


def list_subnets() -> list[tuple[ipaddress.IPv4Network, str]]:
    """Return [(net, interface_label)] for each non-loopback IPv4 /24-ish."""
    subnets: list[tuple[ipaddress.IPv4Network, str]] = []
    seen: set[str] = set()
    for if_name, addrs in psutil.net_if_addrs().items():
        for snic in addrs:
            if snic.family != socket.AF_INET:
                continue
            ip = snic.address
            if ip.startswith("127.") or ip.startswith("169.254."):
                continue
            try:
                net = ipaddress.IPv4Network(f"{ip}/24", strict=False)
            except Exception:
                continue
            key = str(net)
            if key in seen:
                continue
            seen.add(key)
            low = if_name.lower()
            if "wi-fi" in low or "wlan" in low or "wireless" in low:
                label = "WiFi"
            elif "hotspot" in low or low.startswith("ap"):
                label = "Hotspot"
            elif "tether" in low or "rndis" in low or "usb" in low:
                label = "USB Tether"
            else:
                label = if_name
            subnets.append((net, label))
    return subnets


async def _ping(ip: str) -> int | None:
    """Return RTT in ms, or None if unreachable."""
    if IS_WINDOWS:
        args = ["ping", "-n", "1", "-w", str(PING_TIMEOUT_MS), ip]
    else:
        args = ["ping", "-c", "1", "-W", "1", ip]
    start = time.perf_counter()
    try:
        proc = await asyncio.create_subprocess_exec(
            *args,
            stdout=asyncio.subprocess.DEVNULL,
            stderr=asyncio.subprocess.DEVNULL,
        )
        rc = await asyncio.wait_for(proc.wait(), timeout=PING_TIMEOUT_MS / 1000 + 1.0)
    except (asyncio.TimeoutError, FileNotFoundError, Exception):
        return None
    if rc != 0:
        return None
    return max(1, int((time.perf_counter() - start) * 1000))


IP_WEBCAM_PORT = 8080


async def _fingerprint_ipwebcam(ip: str) -> dict | None:
    """Probe port 8080 for the IP Webcam app on Android.  Returns a phone
    identity packet if the server banner matches, else None.  Used to
    identify the phone even when sensor_node.py (Termux) isn't running."""
    try:
        reader, writer = await asyncio.wait_for(
            asyncio.open_connection(ip, IP_WEBCAM_PORT),
            timeout=WS_TIMEOUT_S,
        )
    except (asyncio.TimeoutError, OSError, Exception):
        return None
    try:
        writer.write(b"GET / HTTP/1.0\r\nHost: " + ip.encode() + b"\r\n\r\n")
        try:
            await asyncio.wait_for(writer.drain(), timeout=WS_TIMEOUT_S)
        except asyncio.TimeoutError:
            return None
        try:
            data = await asyncio.wait_for(reader.read(512), timeout=WS_TIMEOUT_S)
        except asyncio.TimeoutError:
            return None
        if b"IP Webcam" in data or b"ipwebcam" in data.lower():
            return {"node_type": "phone", "node_id": "SENSOR_ALPHA"}
        return None
    finally:
        try:
            writer.close()
            await asyncio.wait_for(writer.wait_closed(), timeout=0.2)
        except Exception:
            pass


async def _fingerprint(ip: str) -> dict | None:
    """Try WebSocket on :8765, read first packet, return its content or None."""
    uri = f"ws://{ip}:{SCAN_PORT}"
    try:
        async with websockets.connect(uri, open_timeout=WS_TIMEOUT_S, close_timeout=0.2) as ws:
            try:
                raw = await asyncio.wait_for(ws.recv(), timeout=WS_TIMEOUT_S)
            except asyncio.TimeoutError:
                return {"node_type": "unknown", "node_id": "???"}
            if isinstance(raw, bytes):
                return {"node_type": "unknown", "node_id": "???"}
            try:
                packet = json.loads(raw)
            except json.JSONDecodeError:
                return {"node_type": "unknown", "node_id": "???"}
            return {
                "node_type": str(packet.get("node_type", "unknown")),
                "node_id":   str(packet.get("node_id", "???")),
            }
    except Exception:
        return None


async def _probe_host(ip: str, iface_label: str) -> NetworkNode | None:
    rtt = await _ping(ip)
    if rtt is None:
        return None
    ws_info, cam_info = await asyncio.gather(
        _fingerprint(ip), _fingerprint_ipwebcam(ip),
        return_exceptions=True,
    )
    if isinstance(ws_info, BaseException): ws_info = None
    if isinstance(cam_info, BaseException): cam_info = None
    # WS probe wins (real node identity); IP Webcam is a fallback to identify
    # the phone even when sensor_node.py isn't running in Termux yet.
    info = ws_info or cam_info
    if info is None:
        role = "UNKNOWN CONTACT"
        node_id = "???"
        node_type = "unknown"
        status = "ICMP_ONLY"
    else:
        node_type = info["node_type"]
        node_id   = info["node_id"]
        role = ROLE_BY_TYPE.get(node_type, "UNKNOWN CONTACT")
        status = "ONLINE"
    return NetworkNode(
        ip=ip,
        role=role,
        node_id=node_id,
        node_type=node_type,
        status=status,
        latency_ms=rtt,
        interface=iface_label,
    )


async def _scan_subnet(net: ipaddress.IPv4Network, label: str) -> list[NetworkNode]:
    sem = asyncio.Semaphore(PING_CONCURRENCY)

    async def _bounded(ip_str: str):
        async with sem:
            return await _probe_host(ip_str, label)

    tasks = [_bounded(str(ip)) for ip in net.hosts()]
    results = await asyncio.gather(*tasks, return_exceptions=True)
    nodes: list[NetworkNode] = []
    for r in results:
        if isinstance(r, NetworkNode):
            nodes.append(r)
    return nodes


def _print_banner(subnets: list[tuple[ipaddress.IPv4Network, str]]) -> None:
    print("\n╔══════════════════════════════════════════════════════════╗")
    print("║         ASTARTES NETWORK SCAN — INITIATING               ║")
    print("║             MECHANICUS AUGUR ARRAY ACTIVE                ║")
    print("╠══════════════════════════════════════════════════════════╣")
    print("║  SCANNING SUBNETS:                                       ║")
    for net, label in subnets:
        line = f"    {str(net):<18} ({label})"
        print(f"║  {line:<56}║")
    print("╠══════════════════════════════════════════════════════════╣")


def _print_nodes(nodes: list[NetworkNode]) -> None:
    for n in nodes:
        latency = f"{n.latency_ms}ms" if n.status == "ONLINE" else "---"
        line = f"{n.ip:<15} {n.role:<18} [{n.node_id}]"
        # Right-pad to fixed width so the border lines up for both numeric
        # latency ("4ms") and the placeholder ("---").
        print(f"║  {line:<46}{latency:>7}  ║")
    print("╠══════════════════════════════════════════════════════════╣")
    brothers = sum(1 for n in nodes if n.status == "ONLINE")
    unknown  = sum(1 for n in nodes if n.status != "ONLINE")
    nav      = "FULL" if any(n.node_type == "pc" for n in nodes) else (
               "FALLBACK" if any(n.node_type == "relay" for n in nodes) else "OFFLINE")
    print(f"║  BATTLE-BROTHERS IDENTIFIED: {brothers:<28}║")
    print(f"║  UNKNOWN CONTACTS:            {unknown:<28}║")
    print(f"║  NAV LINK STATUS:             {nav:<28}║")
    print("╚══════════════════════════════════════════════════════════╝\n")


async def scan_once(
    subnet_filter: str | None = None,
    quiet: bool = False,
    save: bool = True,
) -> dict:
    """Run a single network scan.  Returns the network map dict."""
    subnets = list_subnets()
    if subnet_filter:
        try:
            target = ipaddress.IPv4Network(subnet_filter, strict=False)
            subnets = [(target, "manual")]
        except Exception:
            print(f"ERROR: invalid subnet {subnet_filter}")
            return {}
    if not subnets:
        if not quiet:
            print("WARNING: no IPv4 subnets discovered")
        return {"scan_time": datetime.now().isoformat(), "nav_link_status": "OFFLINE", "nodes": []}
    if not quiet:
        _print_banner(subnets)

    all_nodes: list[NetworkNode] = []
    for net, label in subnets:
        nodes = await _scan_subnet(net, label)
        all_nodes.extend(nodes)
    all_nodes.sort(key=lambda n: tuple(int(o) for o in n.ip.split(".")))

    if not quiet:
        _print_nodes(all_nodes)

    nav = "FULL" if any(n.node_type == "pc" for n in all_nodes) else (
          "FALLBACK" if any(n.node_type == "relay" for n in all_nodes) else "OFFLINE")
    result = {
        "scan_time": datetime.now().isoformat(),
        "nav_link_status": nav,
        "nodes": [asdict(n) for n in all_nodes],
    }
    if save:
        try:
            with open(MAP_PATH, "w", encoding="utf-8") as fh:
                json.dump(result, fh, indent=2)
        except OSError as e:
            print(f"WARNING: could not write {MAP_PATH}: {e}")
    return result


async def monitor_forever(
    callback: Callable[[dict], Awaitable[None] | None] | None = None,
    interval: float = 30.0,
) -> None:
    """Re-scan every `interval` seconds, print only changes."""
    last: dict[str, dict] = {}
    while True:
        result = await scan_once(quiet=True)
        current = {n["ip"]: n for n in result.get("nodes", [])}
        for ip, node in current.items():
            if ip not in last:
                print(f"NEW CONTACT:  {ip:<15} — {node['role']:<18} [{node['node_id']}]")
        for ip, node in last.items():
            if ip not in current:
                print(f"CONTACT LOST: {ip:<15} — {node['role']:<18} [{node['node_id']}]")
        last = current
        if callback is not None:
            try:
                ret = callback(result)
                if asyncio.iscoroutine(ret):
                    await ret
            except Exception as e:
                print(f"WARNING: monitor callback failed: {e}")
        await asyncio.sleep(interval)


def main() -> None:
    p = argparse.ArgumentParser(description="Astartes network scanner")
    p.add_argument("--monitor", action="store_true", help="continuous mode (every 30s)")
    p.add_argument("--subnet", type=str, default=None, help="scan a specific subnet, e.g. 192.168.1.0/24")
    p.add_argument("--interval", type=float, default=30.0, help="monitor interval (seconds)")
    args = p.parse_args()

    try:
        if args.monitor:
            print("MONITOR MODE — Ctrl+C to stop")
            asyncio.run(monitor_forever(interval=args.interval))
        else:
            asyncio.run(scan_once(subnet_filter=args.subnet))
    except KeyboardInterrupt:
        print("\nAUGUR ARRAY — SHUTDOWN")


if __name__ == "__main__":
    main()
