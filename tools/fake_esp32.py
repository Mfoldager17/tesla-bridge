#!/usr/bin/env python3
"""A pretend ESP32 with the ESPHome Tesla BLE firmware, for trying the Bluetooth feature without hardware.

    python3 tools/fake_esp32.py [port]          # default 8099

Then set http://localhost:8099 as the ESP32 address on the dashboard (Settings → Bluetooth).
It speaks the same web-server API as ESPHome: GET /events (Server-Sent Events) and
POST /<domain>/<name>/<action>[?value=…|option=…]. Commands change its state, like the real car would,
and are printed. Type "away" / "home" and Enter to move the car out of / into Bluetooth range.
"""
import json
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8099

# id → (state text, value). Names are the component's English entity names.
STATE = {
    "binary_sensor/BLE Status": ("ON", True),
    "binary_sensor/Asleep": ("OFF", False),
    "binary_sensor/Doors": ("OFF", False),          # on = unlocked
    "binary_sensor/Climate": ("OFF", False),
    "binary_sensor/Boot": ("OFF", False),
    "binary_sensor/Frunk": ("OFF", False),
    "binary_sensor/Charge flap": ("OFF", False),
    "binary_sensor/Windows": ("OFF", False),
    "binary_sensor/User presence": ("OFF", False),
    "sensor/Charge level": ("71 %", 71),
    "sensor/Charge limit": ("80 %", 80),
    "sensor/Current limit": ("16 A", 16),
    "sensor/Range": ("190.50 mi", 190.5),
    "sensor/Odometer": ("8123.40 mi", 8123.4),
    "sensor/Interior": ("17.5 °C", 17.5),
    "sensor/Exterior": ("6.0 °C", 6.0),
    "sensor/BLE Signal": ("-67 dBm", -67),
    "text_sensor/Charging state": ("Stopped", "Stopped"),
    "text_sensor/Shift state": ("P", "P"),
    "text_sensor/Version": ("2026.10.2 Nov 1 2026", "2026.10.2 Nov 1 2026"),
    "switch/Climate": ("OFF", False),
    "switch/Charger": ("OFF", False),
    "switch/Sentry mode": ("OFF", False),
    "switch/Defrost car": ("OFF", False),
    "switch/Steering wheel heat": ("OFF", False),
    "switch/BLE Connection": ("ON", True),
    "lock/Lock car": ("LOCKED", None),
    "cover/Boot": ("CLOSED", 0.0),
    "cover/Frunk": ("CLOSED", 0.0),
    "cover/Charge port": ("CLOSED", 0.0),
    "cover/Vent": ("CLOSED", 0.0),
    "number/Charging limit": ("80", 80),
    "number/Charging amps": ("16", 16),
    "number/Climate temperature": ("21", 21),
    "button/Wake up": ("", None),
    "button/Pair BLE key": ("", None),
    "button/Flash light": ("", None),
    "button/Sound horn": ("", None),
    "select/Cabin overheat protection": ("Off", "Off"),
    "select/Cabin overheat limit": ("High", "High"),
}
clients = []
lock = threading.Lock()


def event(eid):
    st, val = STATE[eid]
    return f"event: state\ndata: {json.dumps({'id': eid, 'state': st, 'value': val})}\n\n"


def set_state(eid, st, val):
    with lock:
        STATE[eid] = (st, val)
        msg = event(eid).encode()
        for c in list(clients):
            try:
                c.wfile.write(msg); c.wfile.flush()
            except OSError:
                clients.remove(c)


def on_off(b):
    return ("ON", True) if b else ("OFF", False)


def act(domain, name, action, q):
    eid = f"{domain}/{name}"
    if eid not in STATE:
        return False
    print(f"  ← {action} {eid} {q or ''}", flush=True)
    if domain == "switch":
        on = action == "turn_on" or (action == "toggle" and not STATE[eid][1])
        set_state(eid, *on_off(on))
        if name == "Climate":
            set_state("binary_sensor/Climate", *on_off(on))
        if name == "Charger":
            set_state("text_sensor/Charging state", *(("Charging",) * 2 if on else ("Stopped",) * 2))
    elif domain == "lock":
        locked = action == "lock"
        set_state(eid, "LOCKED" if locked else "UNLOCKED", None)
        set_state("binary_sensor/Doors", *on_off(not locked))
    elif domain == "cover":
        is_open = action == "open" or (action == "toggle" and STATE[eid][0] == "CLOSED")
        set_state(eid, "OPEN" if is_open else "CLOSED", 1.0 if is_open else 0.0)
        sensor = {"Boot": "Boot", "Frunk": "Frunk", "Charge port": "Charge flap", "Vent": "Windows"}[name]
        set_state(f"binary_sensor/{sensor}", *on_off(is_open))
    elif domain == "number":
        v = float(q.get("value", ["0"])[0])
        set_state(eid, f"{v:g}", v)
        if name == "Charging limit":
            set_state("sensor/Charge limit", f"{v:g} %", v)
        if name == "Charging amps":
            set_state("sensor/Current limit", f"{v:g} A", v)
    elif domain == "select":
        o = q.get("option", [""])[0]
        set_state(eid, o, o)
    elif domain == "button" and name == "Wake up":
        set_state("binary_sensor/Asleep", "OFF", False)
    return True


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def do_GET(self):
        if self.path.split("?")[0] != "/events":
            self.send_error(404); return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        with lock:
            self.wfile.write(b'event: ping\ndata: {"title":"tesla-ble"}\n\n')
            for eid in STATE:
                self.wfile.write(event(eid).encode())
            self.wfile.flush()
            clients.append(self)
        try:
            while True:
                time.sleep(10)
                with lock:
                    self.wfile.write(b"event: ping\ndata: {}\n\n"); self.wfile.flush()
        except OSError:
            with lock:
                if self in clients:
                    clients.remove(self)

    def do_POST(self):
        u = urllib.parse.urlparse(self.path)
        parts = [urllib.parse.unquote(p) for p in u.path.strip("/").split("/")]
        ok = len(parts) == 3 and act(parts[0], parts[1], parts[2], urllib.parse.parse_qs(u.query))
        self.send_response(200 if ok else 404)
        self.send_header("Content-Length", "0")
        self.end_headers()


def console():
    for line in sys.stdin:
        cmd = line.strip()
        if cmd == "away":
            set_state("binary_sensor/BLE Status", "OFF", False); print("  car out of range")
        elif cmd == "home":
            set_state("binary_sensor/BLE Status", "ON", True); print("  car in range")


if __name__ == "__main__":
    threading.Thread(target=console, daemon=True).start()
    print(f"Fake ESP32 on http://localhost:{PORT} – type 'away' or 'home' to move the car", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
