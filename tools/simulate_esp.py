#!/usr/bin/env python3
"""Plays the part of the ESP, so the tray app can be tested without the machine.

    python tools/simulate_esp.py                     broadcast, like the real ESP
    python tools/simulate_esp.py --target 127.0.0.1  only on this PC

Runs through: machine off, missing water, heating up, heated up, a shot, and then loops.
"""

import argparse
import json
import socket
import time


def packet(hx=None, steam=None, target=124, boost=0, heat=False, pump=False, shot=0, raw=None):
    if hx is None:
        return {"dev": "marax", "v": 1, "online": False, "pump": False, "shot": 0,
                "rssi": -55, "uptime": int(time.monotonic()), "bad": 0, "raw": raw or ""}
    target_text = "E2" if target is None else f"{target:03d}"
    line = f"+1.22,{steam:03d},{target_text},{hx:03d},{boost:04d},{int(heat)},{int(pump)}"
    return {"dev": "marax", "v": 1, "online": True, "mode": "+", "fw": "1.22", "hx": hx,
            "steam": steam, "target": target, "boost": boost, "heat": heat, "pump": pump,
            "shot": shot, "rssi": -55, "uptime": int(time.monotonic()), "bad": 0, "raw": line}


def scenario():
    for _ in range(6):
        yield "Maschine aus", packet()
    for _ in range(6):
        yield "Wasser fehlt", packet(hx=28, steam=30, target=None)
    for step in range(25):
        hx = 30 + step * 3
        steam = 40 + step * 4
        yield "heizt auf", packet(hx=hx, steam=min(steam, 124), boost=900 - step, heat=True)
    for step in range(12):
        yield "aufgeheizt", packet(hx=93, steam=124, heat=step % 4 == 0)
    for second in range(10):
        yield "Pumpe laeuft", packet(hx=93 - second // 2, steam=122, heat=True, pump=True, shot=second)
    for step in range(10):
        yield "aufgeheizt", packet(hx=90, steam=123, heat=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--target", default="255.255.255.255", help="address to send to")
    parser.add_argument("--port", type=int, default=4210)
    parser.add_argument("--once", action="store_true", help="stop after one run")
    parser.add_argument("--interval", type=float, default=1.0, help="seconds between packets")
    args = parser.parse_args()

    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        while True:
            for label, data in scenario():
                sock.sendto(json.dumps(data, separators=(",", ":")).encode(), (args.target, args.port))
                print(f"{label:14} hx={data.get('hx', '-')} steam={data.get('steam', '-')} pump={data['pump']}")
                time.sleep(args.interval)
            if args.once:
                return


if __name__ == "__main__":
    main()
