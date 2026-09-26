#!/usr/bin/env bash
# Restore a backup into a database (a fresh one for the monthly drill; production only in an incident, see docs/runbooks).
#   PGURL=postgres://.../rahiq_restore AGE_KEY=/secure/key.txt infra/scripts/restore.sh s3://rahiq-backups/daily/rahiq-<stamp>.dump.age
set -euo pipefail
: "${PGURL:?}" "${AGE_KEY:?}"
src=${1:?backup object}
aws s3 cp "$src" - ${R2_ENDPOINT:+--endpoint-url "$R2_ENDPOINT"} | age -d -i "$AGE_KEY" | pg_restore --clean --if-exists --no-owner --dbname "$PGURL"
# The drill passes only if the numbers add up after restore.
psql "$PGURL" -v ON_ERROR_STOP=1 -At <<'SQL'
SELECT 'orders', count(*) FROM ordering.orders;
SELECT 'orders violating the total equation', count(*) FROM ordering.orders WHERE total <> subtotal - discount + shipping + cod_fee;
SELECT 'stock rows over-reserved', count(*) FROM inventory.batches WHERE qty_reserved > qty_on_hand;
SELECT 'last migration', max(name) FROM infra.schema_migrations;
SQL
