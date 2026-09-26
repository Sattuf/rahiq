# Starts local services and waits until they report healthy.
$ErrorActionPreference = "Stop"
$compose = Join-Path $PSScriptRoot "..\compose\docker-compose.yml"
docker compose -f $compose up -d --wait
docker compose -f $compose ps
