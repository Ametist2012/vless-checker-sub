#!/usr/bin/env bash
# Установщик VLESS Checker.
#
# Репозиторий приватный, поэтому нужен GitHub-токен с доступом на чтение (fine-grained: Contents — Read-only):
#
#   export GITHUB_TOKEN=github_pat_…
#   bash <(curl -fsSL -H "Authorization: Bearer $GITHUB_TOKEN" \
#       https://raw.githubusercontent.com/Ametist2012/vless-checker-sub/main/install.sh)              установка или обновление
#   … тот же запуск с аргументом uninstall                                                            удаление
#
# Ключи: --version v1.2.3 (по умолчанию — последний релиз), --archive файл.tar.gz (поставить из локального архива).
# Если репозиторий станет публичным, токен не нужен.
# Без вопросов (для автоматизации) — задайте SITE_URL, NGINX_SITE_CONF, PANEL_PREFIX в окружении.
# Повторный запуск обновляет сервис: данные в /opt/vlesscheck/data и настройки установки сохраняются.
set -euo pipefail

REPO=${VLESSCHECK_REPO:-Ametist2012/vless-checker-sub}
GITHUB_TOKEN=${GITHUB_TOKEN:-}

APP=/opt/vlesscheck
SITE_ENV=$APP/site.env
SERVICES=(vlesscheck-api vlesscheck-checker)

bold=$'\e[1m' dim=$'\e[2m' green=$'\e[32m' red=$'\e[31m' reset=$'\e[0m'
say() { echo "${bold}==>${reset} $*"; }
die() { echo "${red}Ошибка:${reset} $*" >&2; exit 1; }

# --- аргументы ---

action=install version=latest archive=""
while [[ $# -gt 0 ]]; do
    case $1 in
        install | update) action=install ;;
        uninstall | remove) action=uninstall ;;
        --version) version=${2:?}; shift ;;
        --archive) archive=${2:?}; shift ;;
        -h | --help) sed -n '2,10p' "$0"; exit 0 ;;
        *) die "неизвестный аргумент: $1" ;;
    esac
    shift
done

[[ $EUID -eq 0 ]] || die "запустите от root (sudo)"
command -v systemctl >/dev/null || die "нужен systemd"
for cmd in curl tar python3 openssl; do
    command -v "$cmd" >/dev/null || die "не найдена команда $cmd (Ubuntu/Debian: apt install $cmd)"
done

# есть ли терминал, с которого можно спросить (при запуске из скрипта или cron — нет)
interactive() { { true </dev/tty; } 2>/dev/null; }

# спрашиваем с терминала, иначе берём значение по умолчанию
ask() {
    local prompt=$1 default=$2 answer=""
    if interactive; then
        read -r -p "$prompt [$default]: " answer </dev/tty || true
    fi
    echo "${answer:-$default}"
}

yes_no() { [[ $(ask "$1" "$2") =~ ^[YyДд] ]]; }

# Новый HTTPS-сайт: nginx + бесплатный сертификат Let's Encrypt (certbot, с автопродлением).
# Корень сайта отдаёт 404 — снаружи не видно, что на домене что-то есть, кроме панели по секретному адресу.
create_site() {
    command -v apt-get >/dev/null || die "автоматическая настройка сертификата — только для Ubuntu/Debian"
    local domain email ip resolved conf busy fw
    domain=$(ask "  Домен (его A-запись должна указывать на этот сервер)" "")
    [[ $domain =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?\.[A-Za-z]{2,}$ ]] || die "некорректный домен: «$domain»"
    conf=/etc/nginx/sites-available/$domain
    [[ ! -e $conf ]] || die "$conf уже существует — запустите установку снова и выберите его в списке сайтов"

    ip=$(curl -4 -fsS --max-time 10 https://api.ipify.org 2>/dev/null || true)
    resolved=$(getent ahostsv4 "$domain" 2>/dev/null | awk 'NR == 1 { print $1 }' || true)
    if [[ -n $ip && $resolved != "$ip" ]]; then
        echo "  ${red}Внимание:${reset} $domain указывает на ${resolved:-никуда}, а IP этого сервера — $ip."
        echo "  Let's Encrypt не выдаст сертификат, пока DNS не настроен (обновление записи может занять до часа)."
        yes_no "  Всё равно попробовать? y/N" N || die "настройте A-запись домена и запустите установку снова"
    fi
    email=$(ask "  Email для Let's Encrypt (письма об истечении сертификата; можно оставить пустым)" "")

    say "Ставлю nginx и certbot"
    apt-get update -qq >/dev/null && apt-get install -y -qq nginx certbot python3-certbot-nginx >/dev/null \
        || die "не удалось установить nginx/certbot"
    systemctl enable --now nginx >/dev/null 2>&1 || true
    busy=$(ss -ltnpH 'sport = :80' 2>/dev/null | grep -v nginx || true)
    [[ -z $busy ]] || die "порт 80 занят другой программой — certbot не сможет подтвердить домен: $busy"

    fw=$(ufw status 2>/dev/null || true)
    if [[ $fw == *"Status: active"* ]] && ! grep -qE '^(80|443)/tcp.*ALLOW' <<<"$fw"; then
        if yes_no "  Файрвол ufw закрывает 80/443. Открыть их? Y/n" Y; then
            ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null
        fi
    fi

    cat > "$conf" <<NGINX
server {
    listen 80;
    listen [::]:80;
    server_name $domain;
    location / { return 404; }
}
NGINX
    ln -sf "$conf" "/etc/nginx/sites-enabled/$domain"
    nginx -t >/dev/null 2>&1 && systemctl reload nginx || { rm -f "$conf" "/etc/nginx/sites-enabled/$domain"; die "nginx не принял конфиг сайта"; }

    say "Получаю сертификат для $domain"
    local who=(--register-unsafely-without-email)
    [[ -n $email ]] && who=(-m "$email")
    if ! certbot --nginx -d "$domain" --non-interactive --agree-tos --redirect "${who[@]}"; then
        rm -f "$conf" "/etc/nginx/sites-enabled/$domain"
        systemctl reload nginx || true
        die "сертификат не получен: проверьте A-запись домена и что порт 80 открыт снаружи"
    fi
    echo "  ${green}Сертификат получен${reset}, автопродление — таймер certbot."
    NGINX_SITE_CONF=$conf
    SITE_URL=https://$domain
}

# --- удаление ---

if [[ $action == uninstall ]]; then
    say "Останавливаю и удаляю сервисы"
    systemctl disable --now "${SERVICES[@]}" 2>/dev/null || true
    rm -f /etc/systemd/system/vlesscheck-{api,checker}.service /usr/local/bin/v-chk
    systemctl daemon-reload

    NGINX_SITE_CONF=""
    # shellcheck disable=SC1090
    [[ -f $SITE_ENV ]] && source "$SITE_ENV"
    if [[ -n ${NGINX_SITE_CONF:-} && -f $NGINX_SITE_CONF ]] && grep -q 'snippets/vlesscheck.conf' "$NGINX_SITE_CONF"; then
        say "Убираю строку include из $NGINX_SITE_CONF"
        cp -a "$NGINX_SITE_CONF" "/root/nginx-site.backup-$(date +%Y%m%d-%H%M%S)"
        sed -i '/include snippets\/vlesscheck.conf;/d' "$NGINX_SITE_CONF"
    fi
    rm -f /etc/nginx/snippets/vlesscheck.conf
    if command -v nginx >/dev/null && nginx -t 2>/dev/null; then systemctl reload nginx; fi

    if [[ $(ask "Удалить и данные (базу, настройки, результаты)? y/N" N) =~ ^[YyДд]$ ]]; then
        rm -rf "$APP" /root/vlesscheck-admin-password.txt /root/.vlesscheck-password-handed-over
        userdel vlesscheck 2>/dev/null || true
        say "${green}Удалено полностью.${reset}"
    else
        rm -rf "$APP"/{api,checker,bin}*
        say "${green}Удалено.${reset} Данные оставлены в $APP/data — при повторной установке всё вернётся."
    fi
    exit 0
fi

# --- архив с программой ---

case $(uname -m) in
    x86_64) rid=linux-x64 ;;
    aarch64 | arm64) rid=linux-arm64 ;;
    *) die "архитектура $(uname -m) не поддерживается" ;;
esac

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
if [[ -n $archive ]]; then
    cp "$archive" "$work/app.tar.gz"
elif [[ -n $GITHUB_TOKEN ]]; then
    # приватный репозиторий: файлы релиза отдаёт только API GitHub, по id файла и с токеном
    api="https://api.github.com/repos/$REPO/releases/$([[ $version == latest ]] && echo latest || echo "tags/$version")"
    say "Ищу релиз ${version} в $REPO"
    curl -fsS -H "Authorization: Bearer $GITHUB_TOKEN" -H "Accept: application/vnd.github+json" "$api" -o "$work/release.json" \
        || die "релиз не найден или у токена нет доступа к $REPO (нужно право Contents: Read-only)"
    asset=$(python3 -c "
import json, sys
release = json.load(open(sys.argv[1]))
print(next((a['url'] for a in release.get('assets', []) if a['name'] == sys.argv[2]), ''))
" "$work/release.json" "vlesscheck-$rid.tar.gz")
    [[ -n $asset ]] || die "в релизе нет файла vlesscheck-$rid.tar.gz"
    say "Скачиваю vlesscheck-$rid.tar.gz"
    curl -fL --progress-bar -H "Authorization: Bearer $GITHUB_TOKEN" -H "Accept: application/octet-stream" \
        -o "$work/app.tar.gz" "$asset" || die "не удалось скачать релиз"
else
    if [[ $version == latest ]]; then
        url="https://github.com/$REPO/releases/latest/download/vlesscheck-$rid.tar.gz"
    else
        url="https://github.com/$REPO/releases/download/$version/vlesscheck-$rid.tar.gz"
    fi
    say "Скачиваю $url"
    curl -fL --progress-bar -o "$work/app.tar.gz" "$url" \
        || die "не удалось скачать релиз. Если репозиторий приватный — задайте GITHUB_TOKEN (см. начало install.sh)"
fi
tar -C "$work" -xzf "$work/app.tar.gz"
pkg="$work/vlesscheck-$rid"
[[ -d $pkg/api && -d $pkg/checker ]] || die "в архиве нет сборки под $rid"
say "Версия: $(cat "$pkg/VERSION" 2>/dev/null || echo неизвестна)"

# --- настройки установки: из окружения, из прошлой установки или вопросами ---

env_site=${SITE_URL-__unset__} env_conf=${NGINX_SITE_CONF-__unset__} env_prefix=${PANEL_PREFIX-__unset__}
env_legacy=${LEGACY_SUB_PREFIX-__unset__}
updating=false
if [[ -f $SITE_ENV ]]; then
    # shellcheck disable=SC1090
    source "$SITE_ENV"
    updating=true
fi
[[ $env_site != __unset__ ]] && SITE_URL=$env_site
[[ $env_conf != __unset__ ]] && NGINX_SITE_CONF=$env_conf
[[ $env_prefix != __unset__ ]] && PANEL_PREFIX=$env_prefix
[[ $env_legacy != __unset__ ]] && LEGACY_SUB_PREFIX=$env_legacy

if [[ $updating == false && $env_conf == __unset__ ]]; then
    echo
    say "Подключение к nginx"
    sites=()
    if command -v nginx >/dev/null; then
        while IFS= read -r f; do sites+=("$f"); done < <(grep -lE '^\s*listen\s+[^;]*443' /etc/nginx/sites-enabled/* /etc/nginx/conf.d/*.conf 2>/dev/null | xargs -r -n1 readlink -f | sort -u)
    fi
    NGINX_SITE_CONF=""
    if [[ ${#sites[@]} -eq 0 ]]; then
        echo "  HTTPS-сайтов nginx на сервере нет."
        default=N
        interactive && default=Y
        if yes_no "  Создать сайт на своём домене с бесплатным сертификатом Let's Encrypt? Y/n" $default; then
            create_site
        else
            echo "  Панель будет доступна только на 127.0.0.1:8787 — подключите её к своему прокси сами"
            echo "  (пример — deploy/nginx-vlesscheck.conf.template в репозитории)."
        fi
    else
        echo "  Панель встроится в существующий HTTPS-сайт (добавится одна строка include, с резервной копией):"
        for i in "${!sites[@]}"; do echo "    $((i + 1))) ${sites[$i]}"; done
        echo "    n) новый сайт на другом домене с сертификатом Let's Encrypt"
        echo "    0) не подключать к nginx"
        n=$(ask "  Выбор" 1)
        if [[ $n =~ ^[0-9]+$ && $n -ge 1 && $n -le ${#sites[@]} ]]; then
            NGINX_SITE_CONF=${sites[$((n - 1))]}
        elif [[ $n =~ ^[nNнН]$ ]]; then
            create_site
        fi
    fi
fi
NGINX_SITE_CONF=${NGINX_SITE_CONF:-}

if [[ $updating == false && $env_prefix == __unset__ ]]; then
    PANEL_PREFIX=$(ask "  Префикс адреса (панель будет на https://сайт/<префикс>/<секрет>/)" checker)
fi
PANEL_PREFIX=${PANEL_PREFIX:-checker}
[[ $PANEL_PREFIX =~ ^[A-Za-z0-9_-]+$ ]] || die "префикс: только латиница, цифры, - и _"

if [[ -z ${SITE_URL:-} && -n $NGINX_SITE_CONF ]]; then
    name=$(sed -nE 's/^\s*server_name\s+([^ ;]+).*/\1/p' "$NGINX_SITE_CONF" | grep -v '^_$' | head -n 1 || true)
    SITE_URL=${name:+https://$name}
fi
SITE_URL=${SITE_URL:-}
export SITE_URL NGINX_SITE_CONF PANEL_PREFIX LEGACY_SUB_PREFIX="${LEGACY_SUB_PREFIX:-}"

# --- установка ---

echo
say "Устанавливаю в $APP"
was_running=false
systemctl is-active --quiet vlesscheck-api && was_running=true
# если дальше что-то упадёт — возвращаем прежнюю версию, сервис не должен остаться выключенным
trap '[[ $was_running == true ]] && systemctl start "${SERVICES[@]}"; rm -rf "$work"' ERR
systemctl stop "${SERVICES[@]}" 2>/dev/null || true

VLESSCHECK_SETUP_DIR=$pkg bash "$pkg/setup-server.sh"

for app in api checker; do
    rm -rf "$APP/$app.old"
    [[ -d $APP/$app ]] && mv "$APP/$app" "$APP/$app.old"
    cp -a "$pkg/$app" "$APP/$app"
    chown -R root:root "$APP/$app"
done
systemctl enable --now "${SERVICES[@]}" >/dev/null 2>&1
trap 'rm -rf "$work"' ERR

# --- итог ---

# ждём, пока API начнёт отвечать (иначе nginx первые секунды отдаёт 502)
for _ in $(seq 30); do
    curl -s -o /dev/null --max-time 2 http://127.0.0.1:8787/ && break
    sleep 1
done
for _ in $(seq 20); do
    secret=$(python3 -c "
import sqlite3, json
print(json.loads(sqlite3.connect('$APP/data/vlesscheck.db').execute('select value from kv where key=?', ('settings',)).fetchone()[0])['panel_path'])
" 2>/dev/null) && break
    sleep 1
done
for svc in "${SERVICES[@]}"; do systemctl is-active --quiet "$svc" || die "$svc не запустился: journalctl -u $svc"; done

echo
say "${green}Готово.${reset}"
if [[ -n $SITE_URL ]]; then
    echo "  Панель:  ${bold}${SITE_URL%/}/$PANEL_PREFIX/${secret:-<секрет>}/${reset}"
else
    echo "  Панель:  ${bold}http://127.0.0.1:8787/${secret:-<секрет>}/${reset} ${dim}(только с сервера — подключите к своему прокси)${reset}"
fi
if [[ $updating == false ]]; then
    echo "  Логин:   ${bold}admin${reset}"
    echo "  Пароль:  ${bold}$(sed -n 's/^password: //p' /root/vlesscheck-admin-password.txt)${reset}"
fi
echo "  Меню на сервере (адрес, логин и пароль, состояние): ${bold}v-chk${reset}"
