#!/usr/bin/env bash
set -euo pipefail
# Always creates and destroys its own cluster; never touches operator databases.
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
name="postgresql-sharp-mcp-smoke-$$"
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT

dotnet build "$root/postgresql-sharp-mcp.slnx" -c Release
docker run -d --name "$name" -e POSTGRES_PASSWORD=mcp-disposable-only \
  -p 127.0.0.1::5432 postgres:17-bookworm -c shared_preload_libraries=pg_stat_statements >/dev/null
ready=0
for attempt in {1..60}; do
  if docker exec "$name" pg_isready -U postgres >/dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
if [[ "$ready" != 1 ]]; then docker logs "$name"; exit 1; fi
docker exec "$name" apt-get update -qq
docker exec "$name" apt-get install -y -qq postgresql-17-hypopg
python3 "$root/scripts/smoke.py" --container "$name" --seed
