#!/usr/bin/env sh
# Starts local services and waits until they report healthy.
set -eu
compose="$(dirname "$0")/../compose/docker-compose.yml"
docker compose -f "$compose" up -d --wait
docker compose -f "$compose" ps
