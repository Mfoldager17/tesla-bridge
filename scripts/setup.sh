#!/usr/bin/env bash
# Engangsopsætning: MQTT-brugere og intern TLS til tesla-proxy.
set -euo pipefail
cd "$(dirname "$0")/.."

# 1) Intern selvsigneret TLS til tesla-proxy (kun brugt mellem bridge og proxy)
if [ ! -f config/tesla-proxy/tls-cert.pem ]; then
  openssl req -x509 -nodes -newkey ec -pkeyopt ec_paramgen_curve:secp384r1 \
    -pkeyopt ec_param_enc:named_curve -subj '/CN=tesla-proxy' \
    -addext 'subjectAltName=DNS:tesla-proxy' \
    -keyout config/tesla-proxy/tls-key.pem -out config/tesla-proxy/tls-cert.pem -sha256 -days 3650
  echo "✓ Lavede config/tesla-proxy/tls-*.pem"
fi

# 2) Appens eget nøglepar (prime256v1). Bridgen viser selv den offentlige nøgle på
#    https://<dit domæne>/.well-known/appspecific/com.tesla.3p.public-key.pem
#    Vil du genbruge en eksisterende nøgle (fx HA's tesla_fleet.key), så kopiér den hertil FØR du kører scriptet.
if [ ! -f config/tesla-proxy/fleet-key.pem ]; then
  openssl ecparam -name prime256v1 -genkey -noout -out config/tesla-proxy/fleet-key.pem
  echo "✓ Lavede appens nøgle: config/tesla-proxy/fleet-key.pem (tag den med i din backup)"
fi
chmod 600 config/tesla-proxy/fleet-key.pem config/tesla-proxy/tls-key.pem

# 3) MQTT-brugere – kun hvis du kører den medfølgende Mosquitto (--profile mosquitto).
#    Bruger du din egen broker (fx på Pi'en), opret brugerne dér:
#      mosquitto_passwd /etc/mosquitto/passwd fleet-telemetry
#      mosquitto_passwd /etc/mosquitto/passwd tesla-bridge
if [ "${1:-}" = "--with-mosquitto" ] && [ ! -f config/mosquitto/passwd ]; then
  read -rsp "Kodeord til MQTT-bruger 'fleet-telemetry': " FT; echo
  read -rsp "Kodeord til MQTT-bruger 'tesla-bridge': " TB; echo
  read -rsp "Kodeord til MQTT-bruger 'homeassistant': " HA; echo
  touch config/mosquitto/passwd
  docker run --rm -v "$PWD/config/mosquitto:/m" eclipse-mosquitto:2 sh -c "
    mosquitto_passwd -b /m/passwd fleet-telemetry '$FT' &&
    mosquitto_passwd -b /m/passwd tesla-bridge '$TB' &&
    mosquitto_passwd -b /m/passwd homeassistant '$HA'"
  echo "✓ MQTT-brugere oprettet. Sæt i .env: Bridge__Mqtt__Host=mosquitto, Bridge__Mqtt__Password=<tesla-bridge> og FT_MQTT_PASSWORD=<fleet-telemetry>"
fi

if [ -f .env ]; then
  ./scripts/render-config.sh || true
else
  cp .env.example .env; chmod 600 .env
  echo "✓ Lavede .env – udfyld den, og kør derefter ./scripts/render-config.sh"
fi

if [ ! -f config/fleet-telemetry/certs/fullchain.pem ]; then
  echo "⚠ Læg fullchain.pem + privkey.pem for dit telemetri-domæne i config/fleet-telemetry/certs/"
fi
