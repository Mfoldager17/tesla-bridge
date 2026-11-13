#!/usr/bin/env bash
# Tesla Bridge – guided setup for a Linux server with Docker (e.g. a Raspberry Pi).
#
#   curl -fsSL https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/install.sh | bash
#
# Run it again at any time: it remembers your answers (from .env), so Enter keeps the current value.
# It does everything that can be done from the server:
#   • asks for your domain, car and Tesla app – and prints exactly what to type at developer.tesla.com
#   • MQTT: uses the bridge's built-in broker, or creates users in your existing Mosquitto container
#   • finds your domain's certificate in Nginx Proxy Manager, copies it and renews it weekly (cron)
#   • writes .env, pulls the images, starts everything and checks that the public key is reachable
# What only you can do: create the Tesla app, the DNS record, the reverse proxy host, the router
# port forward, and sign in + pair the key in the dashboard. The script tells you when and how.
#
# Options:  --dir <folder>   where to install (default: where it already runs, else ~/tesla-bridge)
#           --dry-run        show what would happen, change nothing
set -euo pipefail

REPO_RAW="https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main"
DIR=""
DRY_RUN=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dir) DIR="$2"; shift 2 ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

# ---------------------------------------------------------------- helpers
if [[ -t 1 ]]; then B=$'\e[1m'; D=$'\e[2m'; G=$'\e[32m'; Y=$'\e[33m'; R=$'\e[31m'; C=$'\e[36m'; N=$'\e[0m'; else B='' D='' G='' Y='' R='' C='' N=''; fi
TTY=/dev/tty
step()  { printf '\n%s== %s ==%s\n' "$B" "$*" "$N"; }
ok()    { printf '  %s✓%s %s\n' "$G" "$N" "$*"; }
warn()  { printf '  %s!%s %s\n' "$Y" "$N" "$*"; }
fail()  { printf '  %s✗ %s%s\n' "$R" "$*" "$N" >&2; exit 1; }
info()  { printf '  %s\n' "$*"; }
run()   { if [[ $DRY_RUN == 1 ]]; then printf '  %s[dry-run]%s %s\n' "$D" "$N" "$*"; else "$@"; fi; }

ask() {        # ask "Question" "default" -> answer on stdout
  local q="$1" def="${2:-}" a
  if [[ -n "$def" ]]; then read -r -p "  $q [$def]: " a <"$TTY" || true; else read -r -p "  $q: " a <"$TTY" || true; fi
  printf '%s' "${a:-$def}"
}
ask_secret() { # keeps the current value when you just press Enter
  local q="$1" def="${2:-}" a hint=""
  [[ -n "$def" ]] && hint=" [keep current]"
  read -r -s -p "  $q$hint: " a <"$TTY" || true; echo >&2
  printf '%s' "${a:-$def}"
}
confirm() {    # confirm "Question" y|n
  local a; a=$(ask "$1 (y/n)" "${2:-y}")
  [[ "$a" =~ ^[YyJj] ]]
}
env_get() {    # value of KEY in .env (empty if missing)
  [[ -f .env ]] || return 0
  grep -E "^$1=" .env | tail -n1 | cut -d= -f2- || true
}
rand() { head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' | head -c "${1:-32}"; }
lan_ip() { hostname -I 2>/dev/null | awk '{print $1}'; }
need() { command -v "$1" >/dev/null 2>&1 || fail "$1 is missing. $2"; }

# ---------------------------------------------------------------- 1. checks
step "Checking the server"
need docker "Install Docker first: https://docs.docker.com/engine/install/"
docker compose version >/dev/null 2>&1 || fail "docker compose (v2) is missing."
need curl "sudo apt install curl"
need openssl "sudo apt install openssl"
[[ -r $TTY ]] || fail "No terminal to ask questions on. Run the script in an interactive shell."
docker info >/dev/null 2>&1 || fail "Your user can't talk to Docker. Run: sudo usermod -aG docker \$USER, log out and in again."
ok "Docker $(docker version --format '{{.Server.Version}}' 2>/dev/null || echo '?') and docker compose"
[[ $DRY_RUN == 1 ]] && warn "Dry run: nothing is changed."

# ---------------------------------------------------------------- 2. folder + files
step "Folder"
if [[ -z "$DIR" ]]; then
  # already installed? use the folder docker compose runs it from – everyone's folders are different
  DIR=$(docker ps -a --filter "label=com.docker.compose.service=tesla-bridge" \
        --format '{{.Label "com.docker.compose.project.working_dir"}}' 2>/dev/null | head -n1)
  [[ -z "$DIR" && -f docker-compose.yml ]] && grep -q tesla-init docker-compose.yml && DIR=$PWD
  DIR=${DIR:-$HOME/tesla-bridge}
fi
DIR=$(ask "Install in" "$DIR")
run mkdir -p "$DIR"
cd "$DIR" 2>/dev/null || { [[ $DRY_RUN == 1 ]] && cd "$(mktemp -d)"; }
if [[ -f .env ]]; then ok "Found an existing .env – your answers from last time are the defaults"; fi
run curl -fsSLo docker-compose.yml "$REPO_RAW/docker-compose.yml"
ok "docker-compose.yml downloaded (newest version)"

# ---------------------------------------------------------------- 3. domain(s)
step "How the internet reaches you"
info "Two things must be reachable from the internet:"
info "  ${B}HTTPS (443)${N}  Tesla fetches your public key and sends you back after login – any reverse proxy or tunnel"
info "  ${B}TCP 4443${N}     the car streams its data – always a port forward (raw TLS, it can't go through a proxy or HTTP tunnel)"
info "1) Reverse proxy – port 443 is forwarded to a reverse proxy (Nginx Proxy Manager, Caddy, Traefik, nginx …)."
info "   One domain for both, e.g. car.example.com."
info "2) Tunnel – the website part goes through a tunnel (e.g. Cloudflare Tunnel), no port 443 open."
info "   Two domains: one through the tunnel (car.example.com) and one DNS only for the car (car-data.example.com)."
CUR_TEL=$(env_get Bridge__Telemetry__Hostname); CUR_PARTNER=$(env_get Bridge__Tesla__PartnerDomain)
[[ "$CUR_TEL" == *ditdomaene* || "$CUR_TEL" == *example* ]] && CUR_TEL=""
[[ "$CUR_PARTNER" == *example* ]] && CUR_PARTNER=""
DEF=1; [[ -n "$CUR_PARTNER" && "$CUR_PARTNER" != "$CUR_TEL" ]] && DEF=2
ACCESS=$(ask "Choose 1 or 2" "$DEF"); [[ "$ACCESS" == 2 ]] || ACCESS=1

ask_domain() { # ask_domain "Question" "default" -> clean domain on stdout
  local d=$2
  while :; do
    d=$(ask "$1" "$d"); d="${d#https://}"; d="${d#http://}"; d="${d%%/*}"; d="${d,,}"
    [[ "$d" == *.* ]] || { warn "That doesn't look like a domain." >&2; continue; }
    [[ "$d" == *tesla* ]] && { warn "Tesla rejects domains containing \"tesla\" – pick another name, e.g. car.${d#*.}" >&2; d=""; continue; }
    printf '%s' "$d"; return
  done
}
if [[ "$ACCESS" == 1 ]]; then
  info "Your (sub)domain, e.g. car.example.com – the name is up to you, but not containing \"tesla\"."
  info "At Cloudflare it must be ${B}DNS only${N} (grey cloud): the car needs to reach your server directly."
  PARTNER=$(ask_domain "Domain" "$CUR_TEL"); TELEMETRY=$PARTNER
else
  info "Website domain – goes through your tunnel (proxied/orange cloud is fine), e.g. car.example.com"
  PARTNER=$(ask_domain "Website domain" "${CUR_PARTNER:-}")
  info "Car domain – ${B}DNS only${N} (grey cloud), an A record to your public IP, e.g. car-data.example.com"
  while :; do
    TELEMETRY=$(ask_domain "Car domain" "$([[ "$CUR_TEL" != "$PARTNER" ]] && echo "$CUR_TEL")")
    [[ "$TELEMETRY" != "$PARTNER" ]] && break
    warn "It must be a different name: the tunnel owns $PARTNER, so the car can't reach port 4443 there."
  done
fi
DOMAIN=$TELEMETRY   # the certificate is for the name the car connects to

PUBLIC_IP=$(curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || true)
DNS_IP=$(getent ahostsv4 "$TELEMETRY" 2>/dev/null | awk 'NR==1{print $1}' || true)
if [[ -z "$DNS_IP" ]]; then warn "$TELEMETRY doesn't resolve yet. Create an A record → ${PUBLIC_IP:-your public IP} (DNS only)."
elif [[ -n "$PUBLIC_IP" && "$DNS_IP" != "$PUBLIC_IP" ]]; then warn "$TELEMETRY points to $DNS_IP, but your public IP is $PUBLIC_IP. Behind Cloudflare? Set it to DNS only."
else ok "$TELEMETRY → $DNS_IP (your public IP)"; fi
if [[ "$ACCESS" == 2 ]]; then
  if getent hosts "$PARTNER" >/dev/null 2>&1; then ok "$PARTNER resolves (through your tunnel)"
  else warn "$PARTNER doesn't resolve yet – add it as a public hostname in your tunnel (shown at the end)."; fi
fi

# ---------------------------------------------------------------- 4. Tesla app
step "Tesla app"
CLIENT_ID=$(env_get Bridge__Tesla__ClientId); CLIENT_SECRET=$(env_get Bridge__Tesla__ClientSecret)
if [[ -z "$CLIENT_ID" ]]; then
  info "Create a new app at ${C}https://developer.tesla.com${N} → Create application, with:"
  info "  Name                  anything without \"tesla\", e.g. ${B}My Car${N}"
  info "  Allowed Origin        ${B}https://$PARTNER${N}"
  info "  Allowed Redirect URI  ${B}https://$PARTNER/auth/callback${N}"
  info "  Scopes                Vehicle Information, Vehicle Location, Vehicle Commands, Vehicle Charging Management"
  info "You do NOT need to make any keys – the bridge makes them."
fi
CLIENT_ID=$(ask "Client ID" "$CLIENT_ID")
CLIENT_SECRET=$(ask_secret "Client Secret" "$CLIENT_SECRET")
[[ -n "$CLIENT_ID" && -n "$CLIENT_SECRET" ]] || warn "No app details yet – fill them in later by running the script again."

# ---------------------------------------------------------------- 5. car
step "Your car"
VIN=$(env_get Bridge__Vehicles__0__Vin)
while :; do
  VIN=$(ask "VIN (17 characters, in the Tesla app under Software)" "$VIN"); VIN="${VIN^^}"; VIN="${VIN// /}"
  [[ ${#VIN} == 17 ]] && break; warn "A VIN is 17 characters."
done
NAME=$(env_get Bridge__Vehicles__0__Name); NAME=$(ask "Name in Home Assistant (becomes the entity ids, e.g. model_y_*)" "${NAME:-Model Y}")
info "Optional: an ESP32 with Bluetooth where the car parks makes commands and wake-ups free while the car is home"
info "(see docs/ble.md). You can also add it later on the dashboard under Settings → Bluetooth."
BLE_URL=$(ask "ESP32 address, e.g. http://192.168.1.50 (empty = none)" "$(env_get Bridge__Vehicles__0__Ble__Url)")
[[ -n "$BLE_URL" && "$BLE_URL" != http* ]] && BLE_URL="http://$BLE_URL"
BLE_USER=""; BLE_PASS=""
if [[ -n "$BLE_URL" ]]; then
  BLE_USER=$(ask "The ESP32's web_username (from its secrets.yaml)" "$(env_get Bridge__Vehicles__0__Ble__Username)")
  BLE_PASS=$(ask_secret "The ESP32's web_password" "$(env_get Bridge__Vehicles__0__Ble__Password)")
fi

# ---------------------------------------------------------------- 6. MQTT
step "MQTT and Home Assistant"
MOSQ=$(docker ps --format '{{.Names}} {{.Image}}' | awk 'tolower($2) ~ /mosquitto/ {print $1; exit}')
CUR_EMB=$(env_get Bridge__Mqtt__EmbeddedBroker)
info "1) No broker – the bridge's built-in one. Home Assistant uses the ${B}Tesla Bridge${N} integration (HACS)."
if [[ -n "$MOSQ" ]]; then info "2) Your Mosquitto container ${B}$MOSQ${N} – Home Assistant gets the entities via MQTT. Users are created for you."
else info "2) (no Mosquitto container found)"; fi
info "3) Another MQTT broker (you type host, port and users)"
DEF=1; [[ "$CUR_EMB" == "false" ]] && DEF=$([[ -n "$MOSQ" ]] && echo 2 || echo 3)
[[ -z "$CUR_EMB" && -n "$MOSQ" ]] && DEF=2
MODE=$(ask "Choose 1, 2 or 3" "$DEF")
MQTT_HOST=""; MQTT_PORT=1883; MQTT_USER=""; MQTT_PASS=""; FT_USER=""; FT_PASS=""; EMBEDDED=true; DISCOVERY=""
case "$MODE" in
  2)
    [[ -n "$MOSQ" ]] || fail "No Mosquitto container found."
    EMBEDDED=false; DISCOVERY=homeassistant
    MQTT_HOST=$(lan_ip); MQTT_HOST=$(ask "IP the containers use to reach $MOSQ (not localhost)" "$(env_get Bridge__Mqtt__Host | grep -v 'x$' || echo "$MQTT_HOST")")
    HOSTPORT=$(docker port "$MOSQ" 1883/tcp 2>/dev/null | awk -F: 'NR==1{print $NF}')
    MQTT_PORT=${HOSTPORT:-1883}
    [[ -z "$HOSTPORT" ]] && warn "$MOSQ doesn't publish port 1883 – if it uses host networking that's fine."
    # Where is its password file? Read the config Mosquitto was started with (-c), whatever the folder layout
    CONF=$(docker exec "$MOSQ" sh -c "tr '\\0' ' ' < /proc/1/cmdline" 2>/dev/null | awk '{for(i=1;i<NF;i++) if($i=="-c") print $(i+1)}')
    PWFILE=$(docker exec "$MOSQ" sh -c "grep -hE '^[[:space:]]*password_file' ${CONF:-/mosquitto/config/mosquitto.conf} /mosquitto/config/*.conf /etc/mosquitto/*.conf /etc/mosquitto/conf.d/*.conf 2>/dev/null | awk '{print \$2}' | head -n1" || true)
    if [[ -z "$PWFILE" ]]; then
      ok "$MOSQ allows anonymous clients – no users needed"
    else
      MQTT_USER=$(env_get Bridge__Mqtt__Username); MQTT_USER=${MQTT_USER:-tesla-bridge}
      FT_USER=$(env_get Bridge__Telemetry__MqttUsername); FT_USER=${FT_USER:-fleet-telemetry}
      MQTT_PASS=$(env_get Bridge__Mqtt__Password); MQTT_PASS=${MQTT_PASS:-$(rand 24)}
      FT_PASS=$(env_get Bridge__Telemetry__MqttPassword); FT_PASS=${FT_PASS:-$(rand 24)}
      run docker exec "$MOSQ" mosquitto_passwd -b "$PWFILE" "$MQTT_USER" "$MQTT_PASS"
      run docker exec "$MOSQ" mosquitto_passwd -b "$PWFILE" "$FT_USER" "$FT_PASS"
      run docker kill -s HUP "$MOSQ" >/dev/null   # re-reads the password file, no restart
      ok "Users $MQTT_USER and $FT_USER are in $PWFILE (random passwords, saved in .env)"
    fi ;;
  3)
    EMBEDDED=false; DISCOVERY=homeassistant
    MQTT_HOST=$(ask "Broker IP (not localhost)" "$(env_get Bridge__Mqtt__Host)")
    P0=$(env_get Bridge__Mqtt__Port); MQTT_PORT=$(ask "Port" "${P0:-1883}")
    MQTT_USER=$(ask "Username for the bridge (empty = anonymous)" "$(env_get Bridge__Mqtt__Username)")
    MQTT_PASS=$(ask_secret "Password" "$(env_get Bridge__Mqtt__Password)")
    FT_USER=$(ask "Username for fleet-telemetry" "$(env_get Bridge__Telemetry__MqttUsername)")
    FT_PASS=$(ask_secret "Password" "$(env_get Bridge__Telemetry__MqttPassword)") ;;
  *)
    ok "Built-in broker – nothing to set up" ;;
esac

# ---------------------------------------------------------------- 7. .env
step "Writing .env"
TOKEN=$(env_get Bridge__Web__Token); TOKEN=${TOKEN:-$(rand 40)}
TAG=$(env_get BRIDGE_TAG); TAG=${TAG:-latest}
NEW_ENV=$(cat <<EOF
# Written by install.sh $(date '+%Y-%m-%d %H:%M'). Run it again to change answers, or edit by hand.
TZ=$(T=$(env_get TZ); echo "${T:-$(cat /etc/timezone 2>/dev/null || echo Europe/Copenhagen)}")
PUID=$(id -u)
PGID=$(id -g)

Bridge__Telemetry__Hostname=$TELEMETRY
# Only set when the website part has its own name (tunnel); empty = same as Bridge__Telemetry__Hostname
Bridge__Tesla__PartnerDomain=$([[ "$PARTNER" != "$TELEMETRY" ]] && echo "$PARTNER")
Bridge__Telemetry__Port=4443
Bridge__Vehicles__0__Vin=$VIN
Bridge__Vehicles__0__Name=$NAME
Bridge__Vehicles__0__Ble__Url=$BLE_URL
Bridge__Vehicles__0__Ble__Username=$BLE_USER
Bridge__Vehicles__0__Ble__Password=$BLE_PASS
Bridge__Tesla__ClientId=$CLIENT_ID
Bridge__Tesla__ClientSecret=$CLIENT_SECRET

Bridge__Mqtt__EmbeddedBroker=$EMBEDDED
Bridge__Mqtt__Host=$MQTT_HOST
Bridge__Mqtt__Port=$MQTT_PORT
Bridge__Mqtt__Username=$MQTT_USER
Bridge__Mqtt__Password=$MQTT_PASS
Bridge__Telemetry__MqttUsername=$FT_USER
Bridge__Telemetry__MqttPassword=$FT_PASS
Bridge__Mqtt__DiscoveryPrefix=$DISCOVERY

Bridge__Web__Token=$TOKEN
BRIDGE_TAG=$TAG
EOF
)
# keep any other settings you added yourself
if [[ -f .env ]]; then
  EXTRA=$(grep -E '^[A-Za-z_]+=' .env | grep -vE "^($(printf '%s\n' "$NEW_ENV" | grep -oE '^[A-Za-z_0-9]+' | paste -sd'|'))=" || true)
  [[ -n "$EXTRA" ]] && NEW_ENV+=$'\n\n# Your own settings\n'"$EXTRA"
  run install -m 600 .env ".env.bak.$(date +%Y%m%d%H%M%S)"
fi
if [[ $DRY_RUN == 1 ]]; then printf '%s\n' "$NEW_ENV" | sed -E 's/(Secret|Password|Token)=.+/\1=***/' | sed 's/^/    /'
else
  umask 077; printf '%s\n' "$NEW_ENV" > .env
  chmod 600 .env .env.bak.* 2>/dev/null || true   # an existing .env keeps its old mode otherwise
fi
ok ".env written (only readable by you)"

# ---------------------------------------------------------------- 8. certificate
step "Certificate for $DOMAIN"
info "The car only talks to a server with a valid certificate for ${B}$DOMAIN${N}. Nginx Proxy Manager is NOT required –"
info "any Let's Encrypt certificate works. You just need to know the folder it is in."
CERTS=config/fleet-telemetry/certs
run mkdir -p "$CERTS"
covers() { # covers <cert file> <domain> – also accepts a wildcard (*.example.com)
  local san; san=$(sudo openssl x509 -in "$1" -noout -ext subjectAltName 2>/dev/null) || return 1
  grep -qE "DNS:($(printf '%s' "$2" | sed 's/\./\\./g')|\*\.$(printf '%s' "${2#*.}" | sed 's/\./\\./g'))(,|$)" <<<"$san"
}
cert_ok() { [[ -f "$CERTS/fullchain.pem" ]] && openssl x509 -in "$CERTS/fullchain.pem" -noout -checkend 1728000 >/dev/null 2>&1 \
  && covers "$CERTS/fullchain.pem" "$DOMAIN"; }
find_cert() { # find_cert <folder>: sets CERT_FILE and KEY_FILE to the newest valid pair for $DOMAIN anywhere below it
  CERT_FILE=""; KEY_FILE=""
  local f k
  while IFS= read -r f; do
    case "$f" in
      */fullchain.pem|*/fullchain*.pem) k="$(dirname "$f")/privkey${f##*/fullchain}" ;;  # certbot, NPM (live/ and custom_ssl/)
      *.crt) k="${f%.crt}.key" ;;                                                         # Caddy, acme.sh, many others
      *.cer) k="$(dirname "$f")/$(basename "$(dirname "$f")").key" ;;                     # acme.sh fullchain.cer
      *) continue ;;
    esac
    sudo test -f "$k" || continue
    sudo openssl x509 -in "$f" -noout -checkend 0 >/dev/null 2>&1 || continue
    covers "$f" "$DOMAIN" || continue
    CERT_FILE=$f; KEY_FILE=$k; return 0
  done < <(sudo find "$1" -maxdepth 7 \( -name 'fullchain*.pem' -o -name '*.crt' -o -name 'fullchain.cer' \) -not -path '*/archive/*' 2>/dev/null \
           | xargs -r sudo ls -t 2>/dev/null)
  return 1
}
CERT_FILE=""; KEY_FILE=""; RENEW_CMD=""
if cert_ok; then ok "Already in place and valid for more than 20 days"
else
  # 1) Nginx Proxy Manager is found by itself (reverse proxy way only – with a tunnel NPM has no cert for the car domain)
  NPM=$(docker ps --format '{{.Names}} {{.Image}}' | awk 'tolower($2) ~ /nginx-proxy-manager/ {print $1; exit}')
  if [[ -n "$NPM" && "$ACCESS" == 1 ]]; then
    LE=$(docker inspect "$NPM" --format '{{range .Mounts}}{{if eq .Destination "/etc/letsencrypt"}}{{.Source}}{{end}}{{end}}')
    info "Found Nginx Proxy Manager ($NPM) – looking in $LE (needs sudo to read it)"
    [[ -n "$LE" ]] && find_cert "$LE" && ok "Found $CERT_FILE"
  fi
  # 2) otherwise: a folder you know, or get one now
  while [[ -z "$CERT_FILE" ]]; do
    info "Where is the certificate for $DOMAIN?"
    info "  1) In a folder I know – any reverse proxy or ACME client. Examples:"
    info "       Nginx Proxy Manager  <npm data>/letsencrypt         certbot  /etc/letsencrypt"
    info "       Caddy                <caddy data>/certificates      acme.sh  ~/.acme.sh"
    info "     A parent folder is fine – the script searches it for a valid certificate for $DOMAIN."
    info "  2) I don't have one – get it now from Let's Encrypt via Cloudflare DNS (needs a Cloudflare API token)"
    info "  3) Skip for now (run the script again later)"
    CH=$(ask "Choose 1, 2 or 3" "$([[ "$ACCESS" == 2 ]] && echo 2 || echo 1)")
    case "$CH" in
      1)
        P=$(ask "Folder" ""); P="${P/#\~/$HOME}"
        [[ -n "$P" ]] || continue
        if find_cert "$P"; then ok "Found $CERT_FILE"
        else
          warn "No valid certificate for $DOMAIN found below $P."
          if confirm "Type the two file paths yourself instead?" n; then
            CERT_FILE=$(ask "Certificate (full chain, PEM)" ""); KEY_FILE=$(ask "Private key (PEM)" "")
            if ! sudo test -f "$CERT_FILE" || ! sudo test -f "$KEY_FILE"; then warn "One of the files doesn't exist."; CERT_FILE=""; fi
            [[ -n "$CERT_FILE" ]] && ! covers "$CERT_FILE" "$DOMAIN" && warn "That certificate isn't for $DOMAIN – the car will refuse it."
          fi
        fi ;;
      2)
        info "Create a token at ${C}https://dash.cloudflare.com/profile/api-tokens${N} → Create Token → ${B}Edit zone DNS${N},"
        info "limited to the zone of $DOMAIN. It is stored in $DIR/config/letsencrypt/cloudflare.ini (only readable by root)."
        LEDIR="$DIR/config/letsencrypt"
        CFTOKEN=$(ask_secret "Cloudflare API token" "")
        [[ -n "$CFTOKEN" ]] || continue
        EMAIL=$(ask "E-mail for Let's Encrypt (expiry warnings)" "")
        run sudo mkdir -p "$LEDIR"
        if [[ $DRY_RUN == 0 ]]; then
          printf 'dns_cloudflare_api_token = %s\n' "$CFTOKEN" | sudo tee "$LEDIR/cloudflare.ini" >/dev/null
          sudo chmod 600 "$LEDIR/cloudflare.ini"
        fi
        CB=(docker run --rm -v "$LEDIR:/etc/letsencrypt" certbot/dns-cloudflare)
        if [[ -n "$EMAIL" ]]; then MAIL=(-m "$EMAIL"); else MAIL=(--register-unsafely-without-email); fi
        run sudo "${CB[@]}" certonly -n --agree-tos "${MAIL[@]}" \
          --dns-cloudflare --dns-cloudflare-credentials /etc/letsencrypt/cloudflare.ini --dns-cloudflare-propagation-seconds 30 -d "$DOMAIN" \
          || { warn "certbot failed – check the token (Edit zone DNS for the right zone)."; continue; }
        CERT_FILE="$LEDIR/live/$DOMAIN/fullchain.pem"; KEY_FILE="$LEDIR/live/$DOMAIN/privkey.pem"
        RENEW_CMD="${CB[*]} renew -q"
        ok "Certificate issued by Let's Encrypt" ;;
      *) warn "No certificate yet – the car can't send data until there is one. Run the script again when you have it."; break ;;
    esac
  done
  if [[ -n "$CERT_FILE" ]]; then
    # install(1) replaces the target instead of writing through it, so a symlink planted there is never followed.
    run sudo install -m 644 -o "$(id -u)" -g "$(id -g)" "$CERT_FILE" "$CERTS/fullchain.pem"
    run sudo install -m 600 -o "$(id -u)" -g "$(id -g)" "$KEY_FILE" "$CERTS/privkey.pem"
    ok "Copied to $CERTS"
    # Let's Encrypt renews about every 60 days – copy again weekly and restart fleet-telemetry when it changed.
    # The script runs as root (it reads the certificate folder), so it lives root-owned in /usr/local/sbin where
    # nothing else can change it.
    RENEW="/usr/local/sbin/tesla-bridge-renew-cert"
    if [[ $DRY_RUN == 0 ]]; then
      sudo install -m 755 -o root -g root /dev/stdin "$RENEW" <<EOF
#!/bin/sh
# Written by install.sh: keeps the certificate for $DOMAIN in fleet-telemetry up to date.
# Source: $CERT_FILE
set -e
cd "$DIR"
${RENEW_CMD:+$RENEW_CMD}
if ! cmp -s "$CERT_FILE" "$CERTS/fullchain.pem"; then
  install -m 644 -o $(id -u) -g $(id -g) "$CERT_FILE" "$CERTS/fullchain.pem"
  install -m 600 -o $(id -u) -g $(id -g) "$KEY_FILE" "$CERTS/privkey.pem"
  docker compose restart fleet-telemetry
fi
EOF
      rm -f "$DIR/renew-cert.sh"   # the old location (before 1.2)
    fi
    LINE="17 4 * * 1 $RENEW # tesla-bridge-cert"
    if [[ $DRY_RUN == 1 ]]; then info "[dry-run] root crontab += $LINE"
    else (sudo crontab -l 2>/dev/null | grep -v 'tesla-bridge-cert'; echo "$LINE") | sudo crontab -; fi
    ok "Kept up to date every Monday 04:17 (root's crontab, $RENEW)"
  fi
fi

# ---------------------------------------------------------------- 9. start
step "Starting"
run docker compose pull -q
run docker compose up -d
if [[ $DRY_RUN == 0 ]]; then
  for _ in $(seq 1 60); do curl -fsS http://localhost:8090/api/info >/dev/null 2>&1 && break; sleep 2; done
  if curl -fsS http://localhost:8090/api/info >/dev/null 2>&1; then ok "Bridge is running"
  else warn "The bridge doesn't answer yet: docker compose logs tesla-bridge"; fi
  docker compose logs tesla-init 2>/dev/null | grep -iE 'advarsel|warning|mangler|missing' | sed 's/^/    /' || true
fi

# ---------------------------------------------------------------- 10. reachable from the internet?
step "Checking from the outside"
KEYURL="https://$PARTNER/.well-known/appspecific/com.tesla.3p.public-key.pem"
if curl -fsS --max-time 8 "$KEYURL" 2>/dev/null | grep -q "BEGIN PUBLIC KEY"; then
  ok "Public key is reachable at $KEYURL – the bridge registers the domain with Tesla by itself"
else
  warn "The public key isn't reachable at $KEYURL yet."
  if [[ "$ACCESS" == 1 ]]; then
    info "Add a host in your reverse proxy (any will do – Nginx Proxy Manager: Hosts → Add Proxy Host):"
    info "  Domain       ${B}$PARTNER${N}"
    info "  Forward to   ${B}http://$(lan_ip):8091${N}"
    info "  SSL          a Let's Encrypt certificate, force HTTPS"
    info "Then run this script again – it picks up the certificate. The bridge keeps trying to register by itself."
  else
    info "Add a public hostname to your tunnel (Cloudflare: Zero Trust → Networks → Tunnels → your tunnel → Public Hostname):"
    info "  Hostname     ${B}$PARTNER${N}"
    info "  Service      ${B}http://$(lan_ip):8091${N}   (or http://tesla-bridge:8091 if cloudflared is on the same Docker network)"
    info "Or in cloudflared's config.yml:"
    info "  ingress:"
    info "    - hostname: $PARTNER"
    info "      service: http://$(lan_ip):8091"
    info "The bridge keeps trying to register by itself once it is reachable."
  fi
fi
info "Router: forward ${B}TCP 4443${N} to ${B}$(lan_ip)${N} – the car connects to ${B}$TELEMETRY:4443${N} (DNS only, not through a tunnel)."
# fleet-telemetry wants a client certificate (only the car has one), but it shows its own certificate first
TLS=$(timeout 8 openssl s_client -connect "$TELEMETRY:4443" -servername "$TELEMETRY" -verify_hostname "$TELEMETRY" </dev/null 2>/dev/null || true)
if grep -q "Verify return code: 0" <<<"$TLS"; then ok "$TELEMETRY:4443 answers with a valid certificate"
elif grep -q "BEGIN CERTIFICATE" <<<"$TLS"; then warn "$TELEMETRY:4443 answers, but its certificate isn't valid for $TELEMETRY (wrong or expired certificate?)"
else info "(Can't check port 4443 from here – many routers don't loop back. The Setup page shows when the car is streaming.)"; fi

# ---------------------------------------------------------------- done
step "Almost there"
DASH="http://$(lan_ip):8090/?token=$TOKEN"
info "Open the dashboard (only on your own network):"
info "  ${C}$DASH${N}"
info "Under ${B}Setup${N}: 1) Sign in with Tesla  2) scan the QR code with your phone at the car to pair the key."
if [[ "$EMBEDDED" == true ]]; then
  info "Home Assistant: HACS → Custom repositories → https://github.com/Mfoldager17/tesla-bridge (Integration),"
  info "install ${B}Tesla Bridge${N}, restart, add the integration with URL http://$(lan_ip):8090 and the token from .env."
else
  info "Home Assistant: the car shows up by itself under MQTT (if HA uses the same broker)."
fi
info "Update later with: cd $DIR && docker compose pull && docker compose up -d   – or just run this script again."
