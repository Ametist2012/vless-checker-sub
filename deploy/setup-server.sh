#!/usr/bin/env bash
# Настройка сервера (вызывает install.sh от root; повторный запуск безопасен).
# Не трогает другие сервисы и сайты: только /opt/vlesscheck, два systemd-сервиса, меню v-chk и одна строка include
# в nginx-конфиге сайта (с резервной копией и проверкой nginx -t).
# Параметры — из окружения: SITE_URL, PANEL_PREFIX, NGINX_SITE_CONF (пусто — без nginx), LEGACY_SUB_PREFIX.
set -euo pipefail

DIR=${VLESSCHECK_SETUP_DIR:-$(cd "$(dirname "$0")" && pwd)}
PANEL_PREFIX=${PANEL_PREFIX:-checker}
NGINX_SITE_CONF=${NGINX_SITE_CONF:-}
XRAY_VERSION=v26.10.31
case $(uname -m) in
    x86_64) XRAY_ASSET=Xray-linux-64.zip ;;
    aarch64 | arm64) XRAY_ASSET=Xray-linux-arm64-v8a.zip ;;
    *) echo "архитектура $(uname -m) не поддерживается" >&2; exit 1 ;;
esac

# 1. отдельный системный пользователь без входа
id vlesscheck &>/dev/null || useradd --system --home /opt/vlesscheck --shell /usr/sbin/nologin vlesscheck
install -d -o root -g root /opt/vlesscheck /opt/vlesscheck/bin
install -d -o vlesscheck -g vlesscheck -m 750 /opt/vlesscheck/data

# 2. свой xray (xray от 3x-ui не используем)
if [[ ! -x /opt/vlesscheck/bin/xray ]] || ! /opt/vlesscheck/bin/xray version | grep "${XRAY_VERSION#v}" >/dev/null; then
    tmp=$(mktemp -d)
    curl -fsSL -o "$tmp/xray.zip" "https://github.com/XTLS/Xray-core/releases/download/$XRAY_VERSION/$XRAY_ASSET"
    python3 -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extract('xray', sys.argv[2])" "$tmp/xray.zip" "$tmp"
    install -m 755 "$tmp/xray" /opt/vlesscheck/bin/xray
    rm -rf "$tmp"
fi
/opt/vlesscheck/bin/xray version | sed -n 1p  # не head: при pipefail он даёт SIGPIPE и роняет скрипт

# 3. начальный пароль панели: генерируется один раз, копия лежит только у root.
#    Приложение один раз забирает его из data/initial-password (хеширует и удаляет файл);
#    дальше логин и пароль меняются в панели, а файл у root перестаёт быть актуальным.
if [[ ! -f /root/vlesscheck-admin-password.txt ]]; then
    pass=$(openssl rand -base64 18 | tr -d '/+=' | cut -c1-20)
    printf 'login: admin\npassword: %s\n' "$pass" > /root/vlesscheck-admin-password.txt
    chmod 600 /root/vlesscheck-admin-password.txt
fi
if [[ ! -f /root/.vlesscheck-password-handed-over ]]; then
    sed -n 's/^password: //p' /root/vlesscheck-admin-password.txt > /opt/vlesscheck/data/initial-password
    chown vlesscheck:vlesscheck /opt/vlesscheck/data/initial-password
    chmod 600 /opt/vlesscheck/data/initial-password
    touch /root/.vlesscheck-password-handed-over
fi
rm -f /etc/nginx/vlesscheck.htpasswd  # basic auth больше не используется

# 4. nginx: сниппет из шаблона + одна строка include в server { listen 443 } сайта
if [[ -n "$NGINX_SITE_CONF" ]]; then
    {
        sed "s|__PREFIX__|$PANEL_PREFIX|g" "$DIR/nginx-vlesscheck.conf.template"
        if [[ -n "${LEGACY_SUB_PREFIX:-}" ]]; then
            printf '\n# подписки, выданные по старому адресу, продолжают работать\n'
            printf 'location ^~ /%s/sub/ {\n    proxy_pass http://127.0.0.1:8787/sub/;\n    proxy_set_header Host $host;\n' "$LEGACY_SUB_PREFIX"
            printf '    add_header X-Robots-Tag "noindex, nofollow" always;\n}\n'
        fi
    } > /etc/nginx/snippets/vlesscheck.conf
    chmod 644 /etc/nginx/snippets/vlesscheck.conf
    backup=""
    if ! grep -q 'snippets/vlesscheck.conf' "$NGINX_SITE_CONF"; then
        backup="/root/nginx-site.backup-$(date +%Y%m%d-%H%M%S)"
        cp -a "$NGINX_SITE_CONF" "$backup"
        # вставляем сразу после первой строки «listen 443» — она внутри https-блока сайта
        sed -i '0,/^\(\s*\)listen\s\+443.*$/s//&\n\1include snippets\/vlesscheck.conf;/' "$NGINX_SITE_CONF"
    fi
    grep -n 'vlesscheck' "$NGINX_SITE_CONF"
    if ! nginx -t; then
        echo "nginx -t не прошёл — откатываю конфиг сайта" >&2
        [[ -n "$backup" ]] && cp -a "$backup" "$NGINX_SITE_CONF"
        rm -f /etc/nginx/snippets/vlesscheck.conf
        nginx -t
        exit 1
    fi
    systemctl reload nginx
else
    echo "nginx не настраивается: панель доступна только на 127.0.0.1:8787 — подключите её к своему прокси сами"
fi

# 5. меню v-chk: адрес панели, логин/пароль, состояние
install -m 755 "$DIR/v-chk" /usr/local/bin/v-chk
# настройки установки — для v-chk и для обновлений без повторных вопросов
printf 'SITE_URL=%q\nPANEL_PREFIX=%q\nNGINX_SITE_CONF=%q\nLEGACY_SUB_PREFIX=%q\n' \
    "${SITE_URL:-}" "$PANEL_PREFIX" "$NGINX_SITE_CONF" "${LEGACY_SUB_PREFIX:-}" > /opt/vlesscheck/site.env
chmod 644 /opt/vlesscheck/site.env

# 6. systemd
install -m 644 "$DIR/vlesscheck-api.service" "$DIR/vlesscheck-checker.service" /etc/systemd/system/
systemctl daemon-reload
echo "setup ok"
