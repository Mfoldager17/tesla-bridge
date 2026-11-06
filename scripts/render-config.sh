#!/usr/bin/env bash
# Laver config/fleet-telemetry/config.json ud fra skabelonen + værdierne i .env.
# Køres automatisk af setup.sh og update.sh. config.json er ikke i git.
set -euo pipefail
cd "$(dirname "$0")/.."

[ -f .env ] || { echo "✗ Mangler .env (kør ./scripts/setup.sh)"; exit 1; }
set -a; . ./.env; set +a

: "${Bridge__Mqtt__Host:?Sæt Bridge__Mqtt__Host i .env}"
: "${Bridge__Mqtt__Port:=1883}"
: "${FT_MQTT_PASSWORD:=}"

tpl=$(cat config/fleet-telemetry/config.template.json)
for name in Bridge__Mqtt__Host Bridge__Mqtt__Port FT_MQTT_PASSWORD; do
  val="${!name}"
  val="${val//\\/\\\\}"; val="${val//\"/\\\"}"     # JSON-escape \ og "
  tpl="${tpl//"\${$name}"/"$val"}"
done
printf '%s\n' "$tpl" > config/fleet-telemetry/config.json
chmod 600 config/fleet-telemetry/config.json
echo "✓ config/fleet-telemetry/config.json opdateret (broker ${Bridge__Mqtt__Host}:${Bridge__Mqtt__Port})"
