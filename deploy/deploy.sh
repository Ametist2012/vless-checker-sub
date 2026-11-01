#!/usr/bin/env bash
# Сборка и выкладка на VPS: ./deploy/deploy.sh
# Параметры сервера — в deploy/deploy.env (образец: deploy.env.example; в git не попадает).
# Данные (/opt/vlesscheck/data) не трогаются; настройка сервера (setup-server.sh) выполняется при каждой выкладке.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
ENV_FILE="$ROOT/deploy/deploy.env"
[[ -f "$ENV_FILE" ]] || { echo "нет $ENV_FILE — скопируйте deploy.env.example и заполните" >&2; exit 1; }
# shellcheck source=deploy.env.example
source "$ENV_FILE"
HOST=${SSH_HOST:?укажите SSH_HOST в deploy.env}
OUT="$ROOT/.publish"

echo "== сборка панели"
(cd "$ROOT/web" && npm ci --silent && npm run build --silent)

echo "== сборка сервисов под linux-x64"
rm -rf "$OUT"
for app in Api Checker; do
    dotnet publish "$ROOT/src/VlessCheck.$app" -c Release -r linux-x64 --self-contained true \
        -p:DebugType=none -o "$OUT/$(echo $app | tr '[:upper:]' '[:lower:]')" -nologo -v q
done

echo "== выкладка на $HOST"
ssh "$HOST" 'mkdir -p /tmp/vlesscheck-deploy /opt/vlesscheck'
scp -q "$ROOT"/deploy/{setup-server.sh,nginx-vlesscheck.conf.template,vlesscheck-api.service,vlesscheck-checker.service,v-chk} \
    "$ENV_FILE" "$HOST:/tmp/vlesscheck-deploy/"
for app in api checker; do
    rsync -a --delete "$OUT/$app/" "$HOST:/opt/vlesscheck/$app.new/"
done
# сначала останавливаем старую версию и только потом меняем nginx: доступ к панели не должен открыться
# ни на секунду, пока работает версия без нужной защиты (во время выкладки панель отвечает 502)
ssh "$HOST" 'set -e
    # если что-то упадёт до запуска новой версии — поднимаем ту, что была, сервис не должен остаться выключенным
    trap "echo \"выкладка не удалась — запускаю прежнюю версию\" >&2; systemctl start vlesscheck-api vlesscheck-checker" ERR
    systemctl stop vlesscheck-api vlesscheck-checker 2>/dev/null || true
    bash /tmp/vlesscheck-deploy/setup-server.sh
    for app in api checker; do
        rm -rf /opt/vlesscheck/$app.old
        [ -d /opt/vlesscheck/$app ] && mv /opt/vlesscheck/$app /opt/vlesscheck/$app.old
        mv /opt/vlesscheck/$app.new /opt/vlesscheck/$app
    done
    chown -R root:root /opt/vlesscheck/api /opt/vlesscheck/checker
    systemctl enable --now vlesscheck-api vlesscheck-checker
    sleep 3
    systemctl --no-pager --lines=0 status vlesscheck-api vlesscheck-checker | grep -E "●|Active|Memory"
    rm -rf /tmp/vlesscheck-deploy'
secret=$(ssh "$HOST" "python3 -c \"import sqlite3,json; print(json.loads(sqlite3.connect('/opt/vlesscheck/data/vlesscheck.db').execute('select value from kv where key=?', ('settings',)).fetchone()[0])['panel_path'])\"")
echo "== готово"
echo "панель:  ${SITE_URL:-https://<ваш-сайт>}/${PANEL_PREFIX:-checker}/$secret/"
echo "логин, пароль, состояние — меню на сервере: ssh $HOST, затем v-chk"
