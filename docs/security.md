<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · Security</sub>

# Security

Tesla Bridge can unlock your car and open its doors. This page lists what reaches it, what protects each part, and what you should check on your own network.

## What is exposed where

| Port | Who reaches it | What answers |
|---|---|---|
| **4443** (fleet-telemetry) | the internet – the car | Only cars with a certificate Tesla signed (mutual TLS). It only *receives* data |
| **8091** (public listener) | the internet, through your reverse proxy or tunnel | Exactly two paths: the app's public key and the Tesla sign-in callback. Everything else is a 404. The callback only accepts a sign-in started from your dashboard in the last 10 minutes |
| **8090** (dashboard + API) | your LAN only – never forward it | Everything, behind the token (below) |
| **1883** (embedded MQTT broker) | the docker network; published only if you choose to | Logins only, with fixed rights per login (below) |

## The dashboard and API token

- **Always on.** Without `Bridge__Web__Token` in `.env`, the bridge makes a token on first start, saves it in `data/web-token` (readable by you only) and prints it once in the log. Show it again with `docker compose exec tesla-bridge cat /data/web-token`.
- **Home Assistant and scripts** send it as `Authorization: Bearer <token>`. It is never accepted in the URL.
- **The dashboard** trades it once for a session cookie (`HttpOnly`, `SameSite=Strict`), so the token isn't kept in the browser or put in links.
- **Other web sites can't use your browser against it:** state-changing requests from another site (`Sec-Fetch-Site: cross-site`, or an `Origin` that isn't the bridge) are refused.
- **Browser protections:** a Content-Security-Policy, `X-Frame-Options: DENY` (no clickjacking) and `nosniff` on every response.
- Wrong sign-ins are slowed down. Only VINs from `.env` are accepted.
- `Bridge__Web__AllowAnonymous=true` turns the token off, for a network you fully trust. The bridge then only answers requests addressed to an IP address, `localhost`, `*.local` or a name in `Bridge__Web__AllowedHosts`, which stops web pages from reaching it through DNS rebinding.
- The dashboard is plain HTTP. On a LAN you share with others, put it behind your reverse proxy with a certificate, or set `DASHBOARD_BIND=<the server's LAN IP>` in `.env` so it isn't reachable on other interfaces (VPN, second network card).

## MQTT

Anyone who may publish to `tesla/<VIN>/cmd/…` on your broker can send commands. So:

- **The embedded broker** (`Bridge__Mqtt__EmbeddedBroker=true`) never accepts anonymous logins. fleet-telemetry gets a password made by the init container (`data/mqtt-telemetry-password`) and may **only publish raw data under `telemetry/`** – it can't send commands or read anything. `Bridge__Mqtt__Username`/`Password` (e.g. for Home Assistant, if you publish the port) may do everything.
- **Your own Mosquitto** must require logins (`allow_anonymous false`), and should limit fleet-telemetry the same way. In Mosquitto's `acl_file`:

  ```
  user fleet-telemetry
  topic write telemetry/#

  user tesla-bridge
  topic readwrite #

  user homeassistant
  topic readwrite #
  ```
- **The bridge checks this for you.** Shortly after it starts, and then every six hours, it tries to log in to your broker without a username or password. If that works, it warns in the log and at the top of the dashboard's **Setup** page (and counts it in the menu).
- **Not using MQTT commands?** If Home Assistant uses the integration (HTTP), set `Bridge__Mqtt__Commands=false`: then only the HTTP API, with its token, can send commands.

## The ESP32 (Bluetooth)

The ESP32 is a key to your car: its web server can unlock it over Bluetooth, without the internet and without the bridge. `esphome/tesla-ble.yaml` therefore always sets a username and password (`web_username` / `web_password` in `secrets.yaml`). Use a long random password, and if your router can, put the ESP32 on a separate network for smart-home devices. The firmware version is pinned (`ref:`), so a rebuild never pulls in code you haven't looked at. The bridge only talks to an ESP32 at a private address (`192.168.x.x`, `10.x.x.x`, `*.local` …).

## AI assistants (MCP)

[MCP](mcp.md) is off until you turn it on. It has its own token (only its SHA-256 is stored, and it can be revoked on its own), and commands that open the car – unlock, frunk, trunk, windows, charge port, sentry off, guest/valet mode, HomeLink, keyless driving – are refused unless you allow them.

## Keys and files

| File | What it is | Who can read it |
|---|---|---|
| `config/tesla-proxy/fleet-key.pem` | the app's private key: commands are signed with it | tesla-proxy only. **Keep it in your backup** – a new key means pairing the car again |
| `config/tesla-proxy/tls-key.pem` | the internal TLS between the bridge and tesla-proxy | tesla-proxy |
| `config/fleet-telemetry/certs/privkey.pem` | your domain's certificate key | fleet-telemetry |
| `config/public/` | the public key and tesla-proxy's certificate | the bridge (read-only) |
| `data/tokens.json` | your Tesla sign-in (refresh token) | the bridge, 0600 |
| `data/web-token`, `data/mqtt-telemetry-password`, `data/settings.json` | the dashboard token, fleet-telemetry's MQTT password, settings with the ESP32 password | the bridge, 0600 |
| `.env` | your settings and secrets | you (install.sh sets 0600) |

The bridge container gets only `config/public` and `config/bridge` (read-only) and `data` – never the private keys. All containers run without Linux capabilities (`cap_drop: ALL`) and with `no-new-privileges`; only the init container runs as root, to hand the files to your user, and it never follows symlinks while doing so.

## Reporting a problem

Found a security problem? Please open a [GitHub security advisory](https://github.com/Mfoldager17/tesla-bridge/security/advisories/new) instead of a public issue.

---

**Back:** [Guide](README.md)
