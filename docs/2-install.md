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
| Asks **reverse proxy or tunnel**, then checks your domain(s): no "tesla", the car's name points to your public IP and isn't Cloudflare-proxied | Create the DNS record(s) |
| Prints exactly what to type when you create the Tesla app | Create the app at developer.tesla.com and paste the Client ID/Secret |
| MQTT: uses the built-in broker, **or** finds your Mosquitto container and creates both users with random passwords | Pick one |
| Writes `.env` (PUID/PGID, dashboard token, everything else) and backs up the old one | – |
| Certificate: finds it in Nginx Proxy Manager by itself, **or** searches a folder you point to (certbot, Caddy, acme.sh …), **or** gets a new one from Let's Encrypt via Cloudflare DNS. Copies it to fleet-telemetry and keeps it up to date weekly (root crontab, `/usr/local/sbin/tesla-bridge-renew-cert`) | Know where your certificate is – or have a Cloudflare API token ready |
| Pulls the images, starts everything, checks that the public key and port 4443 are reachable from outside, and prints the proxy host or tunnel hostname to add | Add the proxy host or tunnel hostname; forward TCP 4443 in your router |
| Prints a link to the dashboard that signs you in | Sign in with Tesla and pair the key at the car (QR code) |

Registering the domain with Tesla happens by itself once the public key is reachable (the bridge retries every 10 minutes, then every 6 hours; turn it off with `Bridge__Tesla__AutoRegister=false`). It never replaces a different key that is already registered.

Run the script again whenever you like – to change an answer, after creating the proxy host or tunnel hostname, or to update. Your current values are the defaults, so Enter keeps them. `--dry-run` shows what it would do without changing anything; `--dir <folder>` installs somewhere else.

When the script is done: **[3. Connect the car →](3-connect-the-car.md)**

## By hand

### Step 1 – Create the app at Tesla

On [developer.tesla.com](https://developer.tesla.com) → *Create application*:

| Field | Value |
|---|---|
| Name | your choice, just *without* "tesla", e.g. `My Car` |
| Allowed Origin | `https://<your domain>` (e.g. `https://car.example.com` – with a tunnel, the name *through the tunnel*) |
| Allowed Redirect URI | `https://<your domain>/auth/callback` |
| Scopes | Vehicle Information, Vehicle Location, Vehicle Commands, Vehicle Charging Management |

Note the **Client ID** and **Client Secret**. You do *not* create keys yourself: the bridge generates the key pair and serves the public key at `https://<your domain>/.well-known/appspecific/com.tesla.3p.public-key.pem`.

### Step 2 – Domain(s), HTTPS and port 4443

Pick the way that matches how you publish things ([which one?](1-before-you-start.md#choice-1-how-the-internet-reaches-you)). Nginx Proxy Manager is just an example – any reverse proxy works.

<table>
<tr><th>Reverse proxy</th><th>Tunnel (e.g. Cloudflare Tunnel)</th></tr>
<tr><td valign="top">

1. DNS: `car.example.com` → your public IP, **DNS only**.
2. In your reverse proxy: a host `car.example.com` → `http://<server-IP>:8091`, with a Let's Encrypt certificate and HTTPS forced.
   *NPM:* Hosts → Add Proxy Host. *Caddy:*
   ```
   car.example.com {
       reverse_proxy <server-IP>:8091
   }
   ```
3. Router: forward **TCP 4443** → the server.

</td><td valign="top">

1. In your tunnel: a public hostname `car.example.com` → `http://<server-IP>:8091`.
   *Cloudflare:* Zero Trust → Networks → Tunnels → Public Hostname, or in `config.yml`:
   ```yaml
   ingress:
     - hostname: car.example.com
       service: http://<server-IP>:8091
   ```
2. DNS: a **second** name, `car-data.example.com` → your public IP, **DNS only** (grey cloud).
3. Router: forward **TCP 4443** → the server.

</td></tr>
</table>

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
Bridge__Telemetry__Hostname=car.example.com # the name the CAR connects to (step 2)
# Tunnel only – the name through the tunnel; leave it out with a reverse proxy:
# Bridge__Telemetry__Hostname=car-data.example.com
# Bridge__Tesla__PartnerDomain=car.example.com
Bridge__Vehicles__0__Vin=<VIN>              # 17 characters, in the Tesla app under Software
Bridge__Vehicles__0__Name=Model Y           # your choice → device "Model Y", entity ids model_y_*
Bridge__Tesla__ClientId=<Client ID from step 1>
Bridge__Tesla__ClientSecret=<Client Secret from step 1>
Bridge__Web__Token=<token>                  # long random string, e.g.: openssl rand -hex 24 (empty: the bridge makes one)
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

The car talks directly to fleet-telemetry on port 4443, so fleet-telemetry needs a valid certificate for the name the car connects to (`Bridge__Telemetry__Hostname`). **Nginx Proxy Manager is not required** – any Let's Encrypt certificate works. You just need to know which folder it's in. Copy the full chain and the private key to `config/fleet-telemetry/certs/fullchain.pem` and `privkey.pem`:

```bash
SRC=<certificate folder>          # see the table below
mkdir -p config/fleet-telemetry/certs
sudo cp -L "$SRC"/fullchain.pem config/fleet-telemetry/certs/fullchain.pem
sudo cp -L "$SRC"/privkey.pem   config/fleet-telemetry/certs/privkey.pem
sudo chown -R $(id -u):$(id -g) config/fleet-telemetry/certs
```

| Made by | Folder (`SRC`) | Files |
|---|---|---|
| Nginx Proxy Manager | `<letsencrypt>/live/npm-<nr>` – see below | `fullchain.pem`, `privkey.pem` |
| certbot | `/etc/letsencrypt/live/<domain>` | `fullchain.pem`, `privkey.pem` |
| Caddy | `<caddy data>/caddy/certificates/<ca>/<domain>` | `<domain>.crt` → fullchain.pem, `<domain>.key` → privkey.pem |
| acme.sh | `~/.acme.sh/<domain>` (or `<domain>_ecc`) | `fullchain.cer` → fullchain.pem, `<domain>.key` → privkey.pem |
| Traefik | inside `acme.json` | export with e.g. `traefik-certs-dumper` first |

**Finding it in Nginx Proxy Manager** – the folder mounted at `/etc/letsencrypt` in *your* NPM container, and in it the one for your domain:

```bash
LE=$(docker inspect <npm container> --format '{{range .Mounts}}{{if eq .Destination "/etc/letsencrypt"}}{{.Source}}{{end}}{{end}}')
sudo grep -l "<your domain>" "$LE"/renewal/*.conf      # e.g. …/renewal/npm-21.conf → SRC=$LE/live/npm-21
```

**No certificate yet** (typical with a tunnel, because nothing has asked for one for `car-data.example.com`)? Get one with certbot and a Cloudflare API token (*Edit zone DNS*) – no port 80 needed:

```bash
sudo mkdir -p config/letsencrypt
echo "dns_cloudflare_api_token = <token>" | sudo tee config/letsencrypt/cloudflare.ini >/dev/null
sudo chmod 600 config/letsencrypt/cloudflare.ini
sudo docker run --rm -v "$PWD/config/letsencrypt:/etc/letsencrypt" certbot/dns-cloudflare certonly -n --agree-tos \
  -m <your e-mail> --dns-cloudflare --dns-cloudflare-credentials /etc/letsencrypt/cloudflare.ini -d car-data.example.com
# SRC=config/letsencrypt/live/car-data.example.com
```

Let's Encrypt renews roughly every 60 days. Put the copy lines in a weekly cron job (as root), followed by `docker compose restart fleet-telemetry` – with the certbot way, run `… certbot/dns-cloudflare renew -q` first. The setup script writes exactly this as `/usr/local/sbin/tesla-bridge-renew-cert` (owned by root, so nothing else can change what root runs).

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
