#!/usr/bin/env bash
# Henter nyeste version: konfig-skabeloner fra git + færdigbygget image fra ghcr.io.
# Dine filer (.env, nøgler, certs, data/) bliver ikke rørt.
set -euo pipefail
cd "$(dirname "$0")/.."

git pull --ff-only
./scripts/render-config.sh
docker compose pull
docker compose up -d
docker image prune -f >/dev/null
echo "✓ Opdateret. Logs: docker compose logs -f tesla-bridge"
