<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · **Before you start** → [Install](2-install.md) → [Connect the car](3-connect-the-car.md) → [Home Assistant](4-home-assistant.md) → [The dashboard](5-the-dashboard.md)</sub>

# 1. Before you start

> **About the examples in this guide**
> Every concrete value below is an **example** that you replace with your own:
>
> | Example | Replace with |
> |---|---|
> | `car.example.com` | your own (sub)domain. The name is up to you (`car.`, `ev.`, `tlm.` …) – it just must not contain "tesla" |
> | `car-data.example.com` | only with a tunnel: a second (sub)domain for the car, also your choice |
> | `Model Y` / `model_y_` | the name you give your car in `.env` ([more](entities.md)) |
> | `<server-IP>`, `192.168.0.x` | the IP address of your server and your MQTT broker |
> | `mosquitto` | the name of *your* Mosquitto container (`docker ps`) |
> | `~/tesla-bridge` | the folder you install the bridge in – anywhere you like |
> | `<certificate folder>`, `npm-<nr>` | where *your* certificate is (the guide shows how to find it) |
> | `/mosquitto/config/passwd` | the password file of *your* Mosquitto (the guide shows how to find it) |
> | `<VIN>`, `<password 1>`, `<password 2>`, `<token>` | your VIN and your own passwords/token |
>
> Text in `<angle brackets>` must always be replaced – the brackets themselves are not included.

## Two choices

You make two choices. They don't depend on each other – any combination works, and the setup script asks you both.

| Choice | Options |
|---|---|
| **[How does the internet reach you?](#choice-1-how-the-internet-reaches-you)** | **Reverse proxy** (port 443 open) · **Tunnel** (e.g. Cloudflare Tunnel, no port 443) |
| **[How does Home Assistant get the data?](#choice-2-how-home-assistant-gets-the-data)** | **A) MQTT** (your broker) · **B) Integration** (no broker) |

## Choice 1: how the internet reaches you

Two different things must be reachable from the internet, and they travel differently:

| | Who connects | Why | How it gets in |
|---|---|---|---|
| **HTTPS (443)** | Tesla's servers, and your browser once at login | to fetch your public key, and to send you back after you sign in | ordinary HTTPS – a **reverse proxy** *or* a **tunnel** |
| **TCP 4443** | the car | streams its data, with its own client certificate (mTLS) | **always a port forward** in your router, straight to `fleet-telemetry` |

Port 4443 can't go through a reverse proxy or an HTTP tunnel: they open the TLS connection themselves, and then the car's certificate never reaches `fleet-telemetry`. That's why it is a separate port with its own forward. The website part on 443 has no such limit.

| | **Reverse proxy** | **Tunnel** |
|---|---|---|
| For you if … | you already open port 443 to Nginx Proxy Manager, Caddy, Traefik, nginx … | you publish things with Cloudflare Tunnel (or similar) and keep 443 closed |
| Domains | **one**, e.g. `car.example.com`, DNS only, A record → your public IP | **two**: `car.example.com` through the tunnel, and `car-data.example.com` DNS only → your public IP |
| Router | forward 443 (you probably already do) and **4443** | forward **4443** only |
| `.env` | `Bridge__Telemetry__Hostname=car.example.com` | `Bridge__Tesla__PartnerDomain=car.example.com`<br>`Bridge__Telemetry__Hostname=car-data.example.com` |
| Certificate for the car | usually the one your proxy already got for the domain | for `car-data.example.com` – from any ACME client, or the setup script gets one via Cloudflare DNS |

```
Reverse proxy                                        Tunnel
                                                     
Tesla ──https://car.example.com──▶ router:443        Tesla ──https://car.example.com──▶ Cloudflare ══tunnel══▶ cloudflared
          ──▶ your proxy ──▶ tesla-bridge :8091                                        ──▶ tesla-bridge :8091
Car ──car.example.com:4443──▶ router:4443            Car ──car-data.example.com:4443──▶ router:4443
          ──▶ fleet-telemetry                                  ──▶ fleet-telemetry
```

> **Nginx Proxy Manager is not required.** Any reverse proxy or tunnel works. What you *do* need is a valid certificate for the name the car connects to, and **you need to know which folder it's in** – the bridge copies it to `fleet-telemetry`. Typical places:
>
> | Made by | Folder |
> |---|---|
> | Nginx Proxy Manager | the folder mounted at `/etc/letsencrypt` in the NPM container (`live/npm-<nr>/`) |
> | certbot | `/etc/letsencrypt/live/<domain>/` |
> | Caddy | `<caddy data>/caddy/certificates/<ca>/<domain>/` |
> | acme.sh | `~/.acme.sh/<domain>/` (or `<domain>_ecc/`) |
> | Traefik | inside `acme.json` – export it first, e.g. with `traefik-certs-dumper` |
>
> No certificate at all (typical with a tunnel)? The setup script can get one from Let's Encrypt via Cloudflare DNS, without opening port 80.

**No public IP (CGNAT)?** Then port 4443 can't be forwarded at home. You need something that passes raw TCP through, e.g. a small VPS with WireGuard, or a TCP tunnel service that forwards the port untouched.

## Choice 2: how Home Assistant gets the data

Data can reach Home Assistant in **two ways**. Pick the one that suits you:

| | **A) MQTT** | **B) HA integration** |
|---|---|---|
| For you if … | you already run an MQTT broker (Mosquitto, e.g. for Zigbee2MQTT) | you don't have or don't want a broker |
| Extra software | your broker | none |
| In Home Assistant | the MQTT integration (discovery, automatic) | **Tesla Bridge** from HACS |
| Other clients (ESP32, Node-RED, custom dashboards) | read and write MQTT topics | use the HTTP API |
| Entities | ~80 under one device per car | exactly the same names, types and entity ids |

Both ways produce **identical entities**, so you can switch later without touching dashboards or automations ([how](switching.md)). Not sure? Pick **A** if Zigbee2MQTT or anything else already runs on a Mosquitto broker, otherwise **B**. The setup script asks you.

## How it fits together

**Way A – with your own broker**
```
Car ──mTLS:4443──▶ fleet-telemetry ──MQTT──▶ your broker ◀──MQTT──▶ tesla-bridge
                                                  │                     │
                                      HA (MQTT discovery),        tesla-proxy ──▶ Tesla Fleet API ──▶ Car
                                      ESP32, Node-RED …
```

**Way B – without a broker**
```
Car ──mTLS:4443──▶ fleet-telemetry ──MQTT──▶ tesla-bridge (embedded broker, internal to Docker only)
                                                  │
                               HTTP + live events (:8090)
                                                  │
                                 the "Tesla Bridge" HA integration
                                                  │  commands
                                                  ▼
                                    tesla-proxy ──▶ Tesla Fleet API ──▶ Car
```

| Container | From | Job |
|---|---|---|
| `fleet-telemetry` | Tesla (official) | Receives the car's stream (mTLS on port 4443) and puts raw fields on MQTT |
| `tesla-proxy` | Tesla `vehicle-command` | Signs commands with your app's key |
| `tesla-init` | this repo | Runs on every start and exits: creates keys and fleet-telemetry's config from `.env` |
| `tesla-bridge` | this repo (.NET 10) | Normalizes data, HA entities, commands, optimistic state, dashboard, usage |

**What the bridge fixes compared to the Tesla Fleet integration:**
- **No "off → on" flicker.** Press "climate on" and it shows "on" immediately. The car's stale snapshot right after waking does not overwrite it. If the car doesn't confirm within 45 s, it rolls back.
- **Transition states like Teslemetry.** Locks and covers show `locking`/`unlocking`/`opening`/`closing` while the car works.
- **Wakes the car itself.** If the car is asleep, it is woken and the command is resent as soon as it's online.
- **Never "unknown" after a restart.** Last known values are saved in `data/state.json`. When the car wakes, a snapshot is fetched (at most once an hour) for fields it hasn't streamed yet.
- **Runs without HA.** The bridge, dashboard, login and key pairing all work without Home Assistant.

## What you need

- **A server with Docker**, e.g. a Raspberry Pi 4/5 (arm64) or an x86 machine. The image is built for both.
- **A (sub)domain** – or two with a tunnel ([choice 1](#choice-1-how-the-internet-reaches-you)).
  - Tesla does **not** allow the word "tesla" in the domain or the app name, so `tesla.example.com` is rejected. Otherwise the name is up to you.
  - The name the car connects to must be **DNS only** (grey cloud at Cloudflare): the car talks directly to your server.
- **A valid certificate** for the name the car connects to, and the folder it's in (see the box above). NPM is not required.
- **HTTPS to the bridge**: a reverse proxy *or* a tunnel sending `https://<your domain>` to `<server-IP>:8091`. That port *only* answers with the public key and the login callback, never the dashboard.
- **Port forward TCP 4443** from your router to the server. Your existing hosts on 443 are not affected.
- **A new app** on [developer.tesla.com](https://developer.tesla.com) (created in the next step).

Way A only:
- **An MQTT broker** that both the server and HA can reach. Your existing one works fine.

Way B only:
- **HACS** in Home Assistant (or copy the folder manually, see [step 4](4-home-assistant.md#b-the-tesla-bridge-integration)).

Optional, any time later:
- **An ESP32** near where the car parks, for free commands and wake-ups over Bluetooth while the car is home ([Bluetooth](ble.md)).

---

**Next:** [2. Install →](2-install.md)
