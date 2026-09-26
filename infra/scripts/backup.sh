#!/usr/bin/env bash
# Nightly logical backup (devops.md §5), on top of the provider's WAL / point-in-time recovery (14 days).
# Encrypted with age before it leaves the host; uploaded to a separate, versioned R2 bucket.
#   PGURL=postgres://... AGE_RECIPIENT=age1... BACKUP_BUCKET=s3://rahiq-backups infra/scripts/backup.sh
set -euo pipefail
: "${PGURL:?}" "${AGE_RECIPIENT:?}" "${BACKUP_BUCKET:?}"
stamp=$(date -u +%Y%m%dT%H%M%SZ)
file="/tmp/rahiq-$stamp.dump.age"
pg_dump --format=custom --no-owner --no-privileges "$PGURL" | age -r "$AGE_RECIPIENT" > "$file"
aws s3 cp "$file" "$BACKUP_BUCKET/daily/rahiq-$stamp.dump.age" ${R2_ENDPOINT:+--endpoint-url "$R2_ENDPOINT"}
rm -f "$file"
echo "backup ok: rahiq-$stamp"
