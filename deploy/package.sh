#!/usr/bin/env bash
# Сборка архива для установки: ./deploy/package.sh [linux-x64|linux-arm64] [версия]
# Результат: dist/vlesscheck-<rid>.tar.gz — готовые сервисы (self-contained, .NET на сервере не нужен) и файлы установки.
# Его же собирает GitHub Actions для релизов; install.sh скачивает и ставит его.
set -euo pipefail

RID=${1:-linux-x64}
VERSION=${2:-dev}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
STAGE="$ROOT/dist/vlesscheck-$RID"

echo "== панель"
(cd "$ROOT/web" && npm ci --silent && npm run build --silent)

echo "== сервисы под $RID"
rm -rf "$STAGE"
for app in Api Checker; do
    dotnet publish "$ROOT/src/VlessCheck.$app" -c Release -r "$RID" --self-contained true \
        -p:DebugType=none -o "$STAGE/$(echo $app | tr '[:upper:]' '[:lower:]')" -nologo -v q
done

cp "$ROOT"/deploy/{setup-server.sh,nginx-vlesscheck.conf.template,vlesscheck-api.service,vlesscheck-checker.service,v-chk} "$STAGE/"
cp "$ROOT/install.sh" "$STAGE/"
echo "$VERSION" > "$STAGE/VERSION"

# без xattr: tar на macOS иначе добавляет служебные заголовки, на которые ругается GNU tar на сервере
COPYFILE_DISABLE=1 tar --no-xattrs -C "$ROOT/dist" -czf "$ROOT/dist/vlesscheck-$RID.tar.gz" "vlesscheck-$RID"
echo "== готово: dist/vlesscheck-$RID.tar.gz ($(du -h "$ROOT/dist/vlesscheck-$RID.tar.gz" | cut -f1))"
