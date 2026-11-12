<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · **Before you start** → [Install](2-install.md) → [Connect the car](3-connect-the-car.md) → [Home Assistant](4-home-assistant.md) → [The dashboard](5-the-dashboard.md)</sub>

# 1. Before you start

> **About the examples in this guide**
> Every concrete value below is an **example** that you replace with your own:
>
> | Example | Replace with |
> |---|---|
> | `car.example.com` | your own (sub)domain. The name is up to you (`car.`, `ev.`, `tlm.` …) – it just must not contain "tesla" |
> | `Model Y` / `model_y_` | the name you give your car in `.env` ([more](entities.md)) |
> | `<server-IP>`, `192.168.0.x` | the IP address of your server and your MQTT broker |
> | `mosquitto` | the name of *your* Mosquitto container (`docker ps`) |
> | `~/tesla-bridge` | the folder you install the bridge in – anywhere you like |
> | `npm-<nr>`, `<letsencrypt folder>` | where your reverse proxy keeps the certificate (the guide shows how to find it) |
> | `/mosquitto/config/passwd` | the password file of *your* Mosquitto (the guide shows how to find it) |
> | `<VIN>`, `<password 1>`, `<password 2>`, `<token>` | your VIN and your own passwords/token |
>
> Text in `<angle brackets>` must always be replaced – the brackets themselves are not included.

## Two ways into Home Assistant

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

For both ways:

- **A server with Docker**, e.g. a Raspberry Pi 4/5 (arm64) or an x86 machine. The image is built for both.
- **A (sub)domain** pointing to your public IP, e.g. `car.example.com` (example – the name is up to you).
  - Tesla does **not** allow the word "tesla" in the domain or the app name, so `tesla.example.com` will be rejected. Otherwise the subdomain can be anything.
  - On Cloudflare: **DNS only** (grey cloud), not proxied. The car must talk directly to your server.
- **A valid certificate** for the domain (Let's Encrypt, e.g. from Nginx Proxy Manager).
- **A reverse proxy** (NPM or similar) forwarding `https://<your domain>` to `<server-IP>:8091`. That port *only* answers with the public key and the login callback, never the dashboard.
- **Port forward 4443** (TCP) from your router to the server. The car sends its data here. NPM and your existing hosts on 443 are not affected.
  - Cloudflare Tunnel and ngrok (HTTP) do **not** work: they terminate TLS and thereby break the car's mTLS. If you need a tunnel, it must be raw TCP (e.g. Tailscale Funnel in TCP mode or a VPS with WireGuard).
- **A new app** on [developer.tesla.com](https://developer.tesla.com) (created in the next step).

Way A only:
- **An MQTT broker** that both the server and HA can reach. Your existing one works fine.

Way B only:
- **HACS** in Home Assistant (or copy the folder manually, see [step 4](4-home-assistant.md#b-the-tesla-bridge-integration)).

---

**Next:** [2. Install →](2-install.md)
