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

# ---------------------------------------------------------------- 3. domain
step "Your domain"
info "The car sends data to a (sub)domain of yours, e.g. car.example.com. The name is up to you,"
info "but Tesla rejects anything containing \"tesla\". At Cloudflare it must be ${B}DNS only${N} (grey cloud)."
DOMAIN=$(env_get Bridge__Telemetry__Hostname); [[ "$DOMAIN" == *ditdomaene* || "$DOMAIN" == *example* ]] && DOMAIN=""
while :; do
  DOMAIN=$(ask "Domain" "$DOMAIN"); DOMAIN="${DOMAIN#https://}"; DOMAIN="${DOMAIN%%/*}"
  [[ "$DOMAIN" == *.* ]] || { warn "That doesn't look like a domain."; continue; }
  [[ "${DOMAIN,,}" == *tesla* ]] && { warn "Tesla rejects domains containing \"tesla\" – pick another name, e.g. car.${DOMAIN#*.}"; DOMAIN=""; continue; }
  break
done
PUBLIC_IP=$(curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || true)
DNS_IP=$(getent ahostsv4 "$DOMAIN" 2>/dev/null | awk 'NR==1{print $1}' || true)
if [[ -z "$DNS_IP" ]]; then warn "$DOMAIN doesn't resolve yet. Create an A record → ${PUBLIC_IP:-your public IP} (DNS only)."
elif [[ -n "$PUBLIC_IP" && "$DNS_IP" != "$PUBLIC_IP" ]]; then warn "$DOMAIN points to $DNS_IP, but your public IP is $PUBLIC_IP. Behind Cloudflare? Set it to DNS only."
else ok "$DOMAIN → $DNS_IP (your public IP)"; fi

# ---------------------------------------------------------------- 4. Tesla app
step "Tesla app"
CLIENT_ID=$(env_get Bridge__Tesla__ClientId); CLIENT_SECRET=$(env_get Bridge__Tesla__ClientSecret)
if [[ -z "$CLIENT_ID" ]]; then
  info "Create a new app at ${C}https://developer.tesla.com${N} → Create application, with:"
  info "  Name                  anything without \"tesla\", e.g. ${B}My Car${N}"
  info "  Allowed Origin        ${B}https://$DOMAIN${N}"
  info "  Allowed Redirect URI  ${B}https://$DOMAIN/auth/callback${N}"
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

Bridge__Telemetry__Hostname=$DOMAIN
Bridge__Telemetry__Port=4443
Bridge__Vehicles__0__Vin=$VIN
Bridge__Vehicles__0__Name=$NAME
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
  run cp .env ".env.bak.$(date +%Y%m%d%H%M%S)"
fi
if [[ $DRY_RUN == 1 ]]; then printf '%s\n' "$NEW_ENV" | sed -E 's/(Secret|Password|Token)=.+/\1=***/' | sed 's/^/    /'
else umask 077; printf '%s\n' "$NEW_ENV" > .env; fi
ok ".env written (only readable by you)"

# ---------------------------------------------------------------- 8. certificate
step "Certificate for $DOMAIN"
CERTS=config/fleet-telemetry/certs
run mkdir -p "$CERTS"
cert_ok() { [[ -f "$CERTS/fullchain.pem" ]] && openssl x509 -in "$CERTS/fullchain.pem" -noout -checkend 1728000 >/dev/null 2>&1 \
  && openssl x509 -in "$CERTS/fullchain.pem" -noout -ext subjectAltName 2>/dev/null | grep -q "DNS:$DOMAIN"; }
SRC=""
if cert_ok; then ok "Already in place and valid for more than 20 days"
else
  NPM=$(docker ps --format '{{.Names}} {{.Image}}' | awk 'tolower($2) ~ /nginx-proxy-manager/ {print $1; exit}')
  if [[ -n "$NPM" ]]; then
    LE=$(docker inspect "$NPM" --format '{{range .Mounts}}{{if eq .Destination "/etc/letsencrypt"}}{{.Source}}{{end}}{{end}}')
    info "Looking for a certificate for $DOMAIN in Nginx Proxy Manager ($LE) – needs sudo to read it"
    for d in $(sudo find "$LE/live" -mindepth 1 -maxdepth 1 -type d 2>/dev/null); do
      if sudo openssl x509 -in "$d/fullchain.pem" -noout -ext subjectAltName 2>/dev/null | grep -q "DNS:$DOMAIN"; then SRC="$d"; break; fi
    done
    [[ -z "$SRC" ]] && warn "No certificate for $DOMAIN in NPM yet. Create the proxy host first (see below) and run the script again."
  else
    info "No Nginx Proxy Manager container found."
    P=$(ask "Folder with fullchain.pem and privkey.pem for $DOMAIN (empty = skip for now)" "")
    [[ -n "$P" ]] && SRC="$P"
  fi
  if [[ -n "$SRC" ]]; then
    run sudo cp -L "$SRC/fullchain.pem" "$SRC/privkey.pem" "$CERTS/"
    run sudo chown "$(id -u):$(id -g)" "$CERTS/fullchain.pem" "$CERTS/privkey.pem"
    ok "Copied from $SRC"
    # Let's Encrypt renews about every 60 days – copy again weekly and restart fleet-telemetry when it changed
    RENEW="$DIR/renew-cert.sh"
    if [[ $DRY_RUN == 0 ]]; then
      cat > "$RENEW" <<EOF
#!/bin/sh
# Written by install.sh: copies the renewed certificate for $DOMAIN to fleet-telemetry.
set -e
cd "$DIR"
if ! cmp -s "$SRC/fullchain.pem" $CERTS/fullchain.pem; then
  cp -L "$SRC/fullchain.pem" "$SRC/privkey.pem" $CERTS/
  chown $(id -u):$(id -g) $CERTS/fullchain.pem $CERTS/privkey.pem
  docker compose restart fleet-telemetry
fi
EOF
      chmod +x "$RENEW"
    fi
    LINE="17 4 * * 1 $RENEW # tesla-bridge-cert"
    if [[ $DRY_RUN == 1 ]]; then info "[dry-run] root crontab += $LINE"
    else (sudo crontab -l 2>/dev/null | grep -v 'tesla-bridge-cert'; echo "$LINE") | sudo crontab -; fi
    ok "Renewed automatically every Monday 04:17 (root's crontab)"
  fi
fi

# ---------------------------------------------------------------- 9. start
step "Starting"
run docker compose pull -q
run docker compose up -d
if [[ $DRY_RUN == 0 ]]; then
  for _ in $(seq 1 60); do curl -fsS http://localhost:8090/api/info >/dev/null 2>&1 && break; sleep 2; done
  curl -fsS http://localhost:8090/api/info >/dev/null 2>&1 && ok "Bridge is running" || warn "The bridge doesn't answer yet: docker compose logs tesla-bridge"
  docker compose logs tesla-init 2>/dev/null | grep -iE 'advarsel|warning|mangler|missing' | sed 's/^/    /' || true
fi

# ---------------------------------------------------------------- 10. reachable from the internet?
step "Checking from the outside"
KEYURL="https://$DOMAIN/.well-known/appspecific/com.tesla.3p.public-key.pem"
if curl -fsS --max-time 8 "$KEYURL" 2>/dev/null | grep -q "BEGIN PUBLIC KEY"; then
  ok "Public key is reachable at $KEYURL – the bridge registers the domain with Tesla by itself"
else
  warn "The public key isn't reachable at $KEYURL yet."
  info "In your reverse proxy (e.g. Nginx Proxy Manager → Hosts → Add Proxy Host):"
  info "  Domain       ${B}$DOMAIN${N}"
  info "  Forward to   ${B}http://$(lan_ip):8091${N}"
  info "  SSL          request a new Let's Encrypt certificate, Force SSL on"
  info "Then run this script again (it picks up the certificate). The bridge keeps trying to register by itself."
fi
info "Router: forward ${B}TCP 4443${N} to ${B}$(lan_ip)${N} – that's where the car sends its data."

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
