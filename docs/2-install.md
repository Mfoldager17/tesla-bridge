<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · [Before you start](1-before-you-start.md) → **Install** → [Connect the car](3-connect-the-car.md) → [Home Assistant](4-home-assistant.md) → [The dashboard](5-the-dashboard.md)</sub>

# 2. Install

Two ways, same result. Both read and write the same `.env`, so you can start with one and finish with the other.

| | |
|---|---|
| **[With the setup script](#with-the-setup-script)** (recommended) | One command; it asks a few questions and does everything that can be done from the server |
| **[By hand](#by-hand)** | Every step explained, nothing hidden. Six steps |

## With the setup script

On the server, run:

```bash
curl -fsSL https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/install.sh | bash
```

It asks a few questions and does the rest:

| The script does | You do |
|---|---|
| Checks Docker, picks the folder, downloads `docker-compose.yml` | – |
| Checks your domain (no "tesla" in it, DNS points to you, not Cloudflare-proxied) | Create the DNS record |
| Prints exactly what to type when you create the Tesla app | Create the app at developer.tesla.com and paste the Client ID/Secret |
| MQTT: uses the built-in broker, **or** finds your Mosquitto container and creates both users with random passwords | Pick one |
| Writes `.env` (PUID/PGID, dashboard token, everything else) and backs up the old one | – |
| Finds your domain's certificate in Nginx Proxy Manager, copies it to fleet-telemetry and renews it weekly (root crontab, `renew-cert.sh`) | Create the proxy host in NPM (the script shows the values) |
| Pulls the images, starts everything, checks that the public key is reachable from outside | Forward TCP 4443 in your router |
| Prints a link to the dashboard that signs you in | Sign in with Tesla and pair the key at the car (QR code) |

Registering the domain with Tesla happens by itself once the public key is reachable (the bridge retries every 10 minutes, then every 6 hours; turn it off with `Bridge__Tesla__AutoRegister=false`). It never replaces a different key that is already registered.

Run the script again whenever you like – to change an answer, after creating the proxy host, or to update. Your current values are the defaults, so Enter keeps them. `--dry-run` shows what it would do without changing anything; `--dir <folder>` installs somewhere else.

When the script is done: **[3. Connect the car →](3-connect-the-car.md)**

## By hand

### Step 1 – Create the app at Tesla

On [developer.tesla.com](https://developer.tesla.com) → *Create application*:

| Field | Value |
|---|---|
| Name | your choice, just *without* "tesla", e.g. `My Car` |
| Allowed Origin | `https://<your domain>` (e.g. `https://car.example.com`) |
| Allowed Redirect URI | `https://<your domain>/auth/callback` |
| Scopes | Vehicle Information, Vehicle Location, Vehicle Commands, Vehicle Charging Management |

Note the **Client ID** and **Client Secret**. You do *not* create keys yourself: the bridge generates the key pair and serves the public key at `https://<your domain>/.well-known/appspecific/com.tesla.3p.public-key.pem`.

### Step 2 – Domain, certificate and reverse proxy

1. Create a DNS record for your domain (e.g. `car.example.com`) → your public IP (DNS only).
2. In NPM: a new proxy host `<your domain>` → `http://<server-IP>:8091`, with a Let's Encrypt certificate and *Force SSL*.
3. Forward **TCP 4443** from your router to the server.

### Step 3 – Download the compose file and create `.env`

The server **only** needs two files. No source code, no `git`. The bridge is pulled prebuilt from `ghcr.io`.

```bash
mkdir -p ~/tesla-bridge && cd ~/tesla-bridge        # any folder you like
curl -fsSLO https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/docker-compose.yml
curl -fsSL  https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/.env.example -o .env
chmod 600 .env
nano .env
```

Fill in the common values (all values are examples):

```ini
TZ=Europe/Copenhagen
PUID=1000                                   # from `id`
PGID=1000
Bridge__Telemetry__Hostname=car.example.com # YOUR domain from step 2
Bridge__Vehicles__0__Vin=<VIN>              # 17 characters, in the Tesla app under Software
Bridge__Vehicles__0__Name=Model Y           # your choice → device "Model Y", entity ids model_y_*
Bridge__Tesla__ClientId=<Client ID from step 1>
Bridge__Tesla__ClientSecret=<Client Secret from step 1>
Bridge__Web__Token=<token>                  # long random string, e.g.: openssl rand -hex 24
```

Then fill in **either A or B**:

<table>
<tr><th>A) MQTT</th><th>B) Integration (no broker)</th></tr>
<tr><td>

```ini
Bridge__Mqtt__EmbeddedBroker=false
Bridge__Mqtt__Host=192.168.0.x   # YOUR broker's IP
Bridge__Mqtt__Port=1883
Bridge__Mqtt__Username=tesla-bridge
Bridge__Mqtt__Password=<password 1>
Bridge__Telemetry__MqttUsername=fleet-telemetry
Bridge__Telemetry__MqttPassword=<password 2>
Bridge__Mqtt__DiscoveryPrefix=homeassistant
```

Use the IP, **not** `localhost`. The bridge and fleet-telemetry run in containers and must be able to reach the broker.

</td><td>

```ini
Bridge__Mqtt__EmbeddedBroker=true
Bridge__Mqtt__DiscoveryPrefix=
```

The other MQTT fields can stay empty. The bridge starts a small broker of its own that only fleet-telemetry talks to over the docker network. It is not reachable from outside.

</td></tr>
</table>

### Step 4 – MQTT users (A only)

The bridge and fleet-telemetry log in to your broker with a user each. The usernames `tesla-bridge` and `fleet-telemetry` are suggestions – use others if you like, as long as they match here and in `.env`. With Mosquitto in a container (replace `mosquitto` with your container's name):

```bash
# where is the password file? (prints e.g. "password_file /mosquitto/config/passwd")
docker exec mosquitto sh -c 'grep -h password_file /mosquitto/config/*.conf /etc/mosquitto/*.conf 2>/dev/null'

docker exec -it mosquitto mosquitto_passwd /mosquitto/config/passwd tesla-bridge     # <password 1>
docker exec -it mosquitto mosquitto_passwd /mosquitto/config/passwd fleet-telemetry  # <password 2>
docker kill -s HUP mosquitto                                                         # re-read users, no restart
```

- The file-permission warning from `mosquitto_passwd` can be ignored.
- If your broker is anonymous (`allow_anonymous true`), skip this and leave usernames and passwords empty in `.env`.
- Test a login: `docker exec mosquitto mosquitto_sub -u tesla-bridge -P '<password 1>' -t '$SYS/broker/version' -C 1`. It should print a version, not "not authorised".

### Step 5 – The certificate for fleet-telemetry

The car talks directly to fleet-telemetry on port 4443, so it needs your domain's certificate. Put `fullchain.pem` and `privkey.pem` in `config/fleet-telemetry/certs/`.

**From Nginx Proxy Manager** – find where *your* NPM keeps its certificates (the folder mounted at `/etc/letsencrypt`), then the one for your domain:

```bash
LE=$(docker inspect <npm container> --format '{{range .Mounts}}{{if eq .Destination "/etc/letsencrypt"}}{{.Source}}{{end}}{{end}}')
sudo grep -l "<your domain>" "$LE"/renewal/*.conf      # e.g. …/renewal/npm-21.conf → the folder is live/npm-21

mkdir -p config/fleet-telemetry/certs
sudo cp -L "$LE"/live/npm-<nr>/fullchain.pem "$LE"/live/npm-<nr>/privkey.pem config/fleet-telemetry/certs/
sudo chown -R $(id -u):$(id -g) config/fleet-telemetry/certs
```

**From anything else** (Caddy, Traefik, certbot, acme.sh …): copy the domain's full chain and private key to `config/fleet-telemetry/certs/fullchain.pem` and `privkey.pem` the same way. The setup script can do this for you too – it asks for the folder.

Let's Encrypt renews roughly every 60 days, so put the same lines in a cron job (e.g. weekly), followed by `docker compose restart fleet-telemetry`.

### Step 6 – Start

```bash
docker compose up -d
docker compose logs tesla-init          # what was created + warnings
docker compose logs -f tesla-bridge     # A: connected to MQTT 192.168.0.x   B: embedded MQTT broker running
```

`tesla-init` runs first on every start and then exits. It creates the app key (`config/tesla-proxy/fleet-key.pem`) and the internal TLS if they are missing, and writes `config/fleet-telemetry/config.json` from `.env`. Existing keys are never touched.

**Include `config/` and `data/` in your backup.** If you lose `fleet-key.pem`, the key has to be paired with the car again.

---

**Next:** [3. Connect the car →](3-connect-the-car.md)
