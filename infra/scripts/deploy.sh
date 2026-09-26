#!/usr/bin/env bash
# Deploy or roll back on the production host (devops.md §3). One command either way:
#   infra/scripts/deploy.sh <git-sha>        deploy that image tag
#   infra/scripts/deploy.sh --rollback       go back to the previously deployed tag
# Order: pull → migrate (forward-only, reviewed SQL; ADR-012) → roll api/worker → wait for /health/ready → roll web.
# Migrations must stay compatible with the previous release (expand/contract), so a rollback never needs a down-migration.
set -euo pipefail

COMPOSE="docker compose -f $(dirname "$0")/../compose/docker-compose.prod.yml"
STATE=/var/lib/rahiq
mkdir -p "$STATE"

if [[ "${1:-}" == "--rollback" ]]; then
  TAG=$(cat "$STATE/previous_tag" 2>/dev/null || { echo "no previous tag recorded" >&2; exit 1; })
  echo "Rolling back to $TAG"
  SKIP_MIGRATE=1
else
  TAG=${1:?usage: deploy.sh <git-sha> | --rollback}
  SKIP_MIGRATE=0
fi

# Deploy freeze: in the last week before a religious holiday, features wait (devops.md §3).
if [[ -f "$STATE/freeze" && "${FORCE_HOTFIX:-0}" != "1" ]]; then
  echo "Deploy freeze is on ($(cat "$STATE/freeze")). Set FORCE_HOTFIX=1 for an urgent fix." >&2
  exit 1
fi

export IMAGE_TAG=$TAG
$COMPOSE pull api worker web

if [[ $SKIP_MIGRATE == 0 ]]; then
  echo "Applying migrations"
  $COMPOSE --profile jobs run --rm migrate
fi

wait_ready() {
  local service=$1 path=$2 port=$3
  for _ in $(seq 1 60); do
    if $COMPOSE exec -T "$service" sh -c "wget -qO- http://127.0.0.1:$port$path >/dev/null 2>&1 || curl -fsS http://127.0.0.1:$port$path >/dev/null 2>&1"; then
      return 0
    fi
    sleep 2
  done
  echo "$service did not become ready" >&2
  return 1
}

# Recreate API then web. Caddy holds requests for up to 15 s while an upstream restarts (lb_try_duration), and
# `--wait` returns only once the new containers pass their health checks.
$COMPOSE up -d --no-deps --wait api
wait_ready api /health/ready 8080
$COMPOSE up -d --no-deps --wait web
wait_ready web /robots.txt 3000
$COMPOSE up -d --no-deps worker

current=$(cat "$STATE/current_tag" 2>/dev/null || true)
if [[ -n "$current" && "$current" != "$TAG" ]]; then echo "$current" > "$STATE/previous_tag"; fi
echo "$TAG" > "$STATE/current_tag"
echo "Deployed $TAG"
