#!/usr/bin/env bash
set -euo pipefail

# Consistent backup of a running Loomi deployment, taken without stopping the service.
#
# Backs up exactly three things (see the volume table in README.md):
#   - the SQLite database, via sqlite3's own .backup command run inside the container. Copying
#     loomi.db directly while WAL is active can miss commits that are not yet in the main file;
#     .backup cannot, because it is SQLite itself producing the snapshot.
#   - the Data Protection keys under /data/keys (part of the same "database" volume as the DB).
#     Without them, every signed-in session cookie is invalidated by a restore. Nothing else rides on
#     them: passwords are hashed in the database itself, not protected by these keys.
#   - the images volume (/data/images): generated images and uploaded reference images.
#
# Deliberately NOT backed up: the browser-profiles volume (ChatGPT logins). It is the largest volume
# by far, nothing else depends on it, and recovering it after a restore is the same one-time step as
# a first setup — reconnect each account from Settings and log in again in the secure browser.
# Restoring an old profile snapshot instead risks handing ChatGPT a session it has already flagged.
#
# Usage: deploy/backup.sh [output-directory]   (default: ./backups)

cd "$(dirname "$0")/.."

OUT_DIR="${1:-backups}"
mkdir -p "$OUT_DIR"

if ! docker compose ps --status running --services 2>/dev/null | grep -qx loomi; then
  echo "Error: the loomi service does not appear to be running (docker compose ps). Start it first." >&2
  exit 1
fi

STAMP="$(date -u +%Y%m%d-%H%M%S)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "Taking a consistent SQLite snapshot..."
REMOTE_SNAPSHOT="/data/loomi-backup-$STAMP.db"
docker compose exec -T loomi sqlite3 /data/loomi.db ".backup $REMOTE_SNAPSHOT"
docker compose cp "loomi:$REMOTE_SNAPSHOT" "$WORK/loomi.db"
docker compose exec -T loomi rm -f "$REMOTE_SNAPSHOT"

echo "Copying Data Protection keys..."
docker compose cp loomi:/data/keys "$WORK/keys"

echo "Copying the images volume (usually the largest part)..."
docker compose cp loomi:/data/images "$WORK/images"

cat > "$WORK/RESTORE.md" <<'NOTE'
# بازیابی نسخه پشتیبان Loomi

این بایگانی شامل سه چیز است:

- `loomi.db` — یک عکس فوری سازگار از پایگاه‌داده (با دستور `.backup` خود SQLite گرفته شده، نه کپی مستقیم فایل زنده در حالی که WAL فعال است)
- `keys/` — کلیدهای Data Protection، از مسیر `/data/keys` داخل کانتینر
- `images/` — تصویرهای تولیدشده و بارگذاری‌شده، از مسیر `/data/images` داخل کانتینر

**عمداً داخل این بایگانی نیست:** پروفایل‌های مرورگر (نشست ورود هر حساب ChatGPT). حجیم‌ترین volume همین است و بازسازی‌اش بعد از بازیابی دقیقاً همان کاری است که در اولین راه‌اندازی هم انجام می‌شود: از Settings روی هر حساب Connect بزنید و در مرورگر امن دوباره وارد شوید.

## مراحل بازیابی

روی همان سروری که Loomi با `docker compose` اجرا می‌شود، از پوشه بایگانی این‌ها را اجرا کنید:

```bash
docker compose stop loomi
docker compose cp loomi.db loomi:/data/loomi.db
docker compose cp keys/. loomi:/data/keys
docker compose cp images/. loomi:/data/images
docker compose start loomi
docker compose logs -f loomi
```

اگر فایل‌های قدیمی `loomi.db-wal` یا `loomi.db-shm` از قبل در volume باقی مانده باشند، جای نگرانی نیست: چون به این `loomi.db` تازه تعلق ندارند، SQLite هنگام باز کردن دیتابیس به‌خاطر عدم تطابق salt آن‌ها را نادیده می‌گیرد.

بعد از بالا آمدن سرویس، با کاربر owner و کلید دسترسی همیشگی وارد شوید، سپس هر حساب ChatGPT را از Settings دوباره Connect و در مرورگر امن وارد کنید.
NOTE

ARCHIVE="$OUT_DIR/loomi-backup-$STAMP.tar.gz"
tar -czf "$ARCHIVE" -C "$WORK" .
chmod 600 "$ARCHIVE"

echo
echo "Backup written to $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))."
echo "It holds real user data and Data Protection keys, not just config — store it somewhere other than this server, and keep its permissions closed (chmod 600, already set)."
echo "Restore steps are inside the archive as RESTORE.md."
