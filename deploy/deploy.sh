#!/usr/bin/env bash
# Выкладка из исходников на свой сервер: ./deploy/deploy.sh
# Собирает тот же архив, что и релиз (package.sh), и ставит его на сервер тем же install.sh.
# Параметры сервера — в deploy/deploy.env (образец: deploy.env.example; в git не попадает).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
ENV_FILE="$ROOT/deploy/deploy.env"
[[ -f "$ENV_FILE" ]] || { echo "нет $ENV_FILE — скопируйте deploy.env.example и заполните" >&2; exit 1; }
# shellcheck source=deploy.env.example
source "$ENV_FILE"
HOST=${SSH_HOST:?укажите SSH_HOST в deploy.env}

case $(ssh "$HOST" uname -m) in
    x86_64) RID=linux-x64 ;;
    aarch64 | arm64) RID=linux-arm64 ;;
    *) echo "архитектура сервера не поддерживается" >&2; exit 1 ;;
esac

"$ROOT/deploy/package.sh" "$RID" "dev-$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo local)"

echo "== выкладка на $HOST"
scp -q "$ROOT/dist/vlesscheck-$RID.tar.gz" "$ROOT/install.sh" "$HOST:/tmp/"
ssh "$HOST" "SITE_URL=$(printf %q "${SITE_URL:-}") NGINX_SITE_CONF=$(printf %q "${NGINX_SITE_CONF:-}") \
    PANEL_PREFIX=$(printf %q "${PANEL_PREFIX:-checker}") LEGACY_SUB_PREFIX=$(printf %q "${LEGACY_SUB_PREFIX:-}") \
    bash /tmp/install.sh --archive /tmp/vlesscheck-$RID.tar.gz; rc=\$?; rm -f /tmp/install.sh /tmp/vlesscheck-$RID.tar.gz; exit \$rc"
