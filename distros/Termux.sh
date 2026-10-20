#!/data/data/com.termux/files/usr/bin/bash
#
# Solace Termux launcher (TUI + CLI).
#
# This script runs in Termux, outside proot-distro. The Solace server itself runs inside
# the Ubuntu proot-distro container and is started as its own background proot session, so
# it keeps running when the TUI exits. proot's --kill-on-exit only reaps the processes of
# the session that launched them, so the server must never be started from the TUI session.

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
ORG='\033[38;5;208m'
CYN='\033[1;36m'
BLU='\033[1;34m'
RST='\033[0m'

GITHUB_REPO="${SOLACE_REPO:-Earth-Restored/Solace}"
GITHUB_BRANCH="${SOLACE_BRANCH:-aspire}"
REMOTE_URL="https://raw.githubusercontent.com/$GITHUB_REPO/refs/heads/$GITHUB_BRANCH/distros/Termux.sh"
SELF_PATH="$(realpath "$0" 2>/dev/null || echo "$0")"
TERMUX_PREFIX="${PREFIX:-/data/data/com.termux/files/usr}"
LAUNCHER_PATH=""
case "$SELF_PATH" in
    "$TERMUX_PREFIX/bin/"*) LAUNCHER_PATH="$SELF_PATH" ;;
esac

# Paths inside the proot-distro Ubuntu container.
PROOT_ROOTFS="$TERMUX_PREFIX/var/lib/proot-distro/containers/ubuntu/rootfs"
UBUNTU_HOME="$PROOT_ROOTFS/root"
SOLACE_DIR="$UBUNTU_HOME/Solace"
CONFIG_DIR="$UBUNTU_HOME/.config/solace"
ENV_FILE="$CONFIG_DIR/termux.env"
SETTINGS_FILE="$CONFIG_DIR/termux-settings.json"
LOG_DIR="$CONFIG_DIR/logs"
STARTUP_JSON="$LOG_DIR/startup.json"
STARTUP_LOG="$LOG_DIR/startup.log"
PID_FILE="$CONFIG_DIR/server.pid"
DASHBOARD_PORT="${SOLACE_DASHBOARD_PORT:-18888}"
DASHBOARD_FORWARD_PID="$CONFIG_DIR/dashboard-forward.pid"
DASHBOARD_SOCAT_PID="$CONFIG_DIR/dashboard-socat.pid"
ADMIN_URL="http://127.0.0.1:5000"

# PostgreSQL runs natively in Termux as the Termux user. proot's fake root cannot run
# initdb or the postmaster (filesystem ownership checks fail), but the container shares
# the network stack with Termux, so the services still reach the database on
# 127.0.0.1:5432.
TERMUX_PGDATA="$TERMUX_PREFIX/var/lib/postgresql"

# Paths as seen by processes inside the container.
UBUNTU_SOLACE_DIR="/root/Solace"
UBUNTU_CONFIG_DIR="/root/.config/solace"
UBUNTU_ENV_FILE="/root/.config/solace/termux.env"
UBUNTU_STARTUP_JSON="/root/.config/solace/logs/startup.json"
UBUNTU_APP_HOST="/root/Solace/src/Solace.AppHost/Solace.AppHost.csproj"
UBUNTU_DOTNET_PRELUDE='export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1'

# ─── SELF-UPDATE ──────────────────────────────────────────

update_self() {
    # Never overwrite a copy that is being run from a git checkout.
    [ -n "$LAUNCHER_PATH" ] || return
    command -v curl >/dev/null 2>&1 || return
    TMP_PATH="$(mktemp "$TERMUX_PREFIX/tmp/.solace_update_XXXXXX" 2>/dev/null || mktemp)"
    curl -fsSL --max-time 10 "$REMOTE_URL" -o "$TMP_PATH" 2>/dev/null || { rm -f "$TMP_PATH"; return; }
    if [ -s "$TMP_PATH" ]; then
        chmod +x "$TMP_PATH"
        if ! cmp -s "$TMP_PATH" "$SELF_PATH"; then
            if command mv -f "$TMP_PATH" "$SELF_PATH" 2>/dev/null; then
                echo "[Solace] updated"
            else
                rm -f "$TMP_PATH"
                return
            fi
            exec "$SELF_PATH" "$@"
        fi
    fi
    rm -f "$TMP_PATH"
}

# ─── HELP ─────────────────────────────────────────────────

show_help() {
    echo -e "${BLU}"
    echo "   _____       __"
    echo "  / ___/____  / /___ _________"
    echo "  \__ \/ __ \/ / __ \`/ ___/ _ \\"
    echo " ___/ / /_/ / / /_/ / /__/  __/"
    echo "/____/\____/_/\__,_/\___/\___/"
    echo -e "${RST}"
    echo "Usage: earth [COMMAND]"
    echo ""
    echo "Commands:"
    echo "  (no args)      Open the TUI menu"
    echo "  help           Show this help message"
    echo "  start          Start the server in the background"
    echo "  stop           Stop the server"
    echo "  status         Show the server status"
    echo "  logs [NAME]    Show logs (optionally for one service)"
    echo "  eula           Accept the Minecraft EULA"
    echo "  eula --delete  Revoke the Minecraft EULA acceptance"
    echo "  uninstall      Completely remove Solace"
    echo ""
    echo "Platform: Termux (Android) - proot-distro + Ubuntu"
    echo "Server:   Aspire AppHost / Web Portal"
    echo "Dashboard: http://<LAN-IP>:18888 (current login link: earth status)"
    echo ""
    exit 0
}

pause() {
    read -r _ < /dev/tty
}

show_banner() {
    echo ""
    echo -e "${BLU}"
    echo "      _____       __"
    echo "     / ___/____  / /___ _________"
    echo "     \__ \/ __ \/ / __ \`/ ___/ _ \\"
    echo "    ___/ / /_/ / / /_/ / /__/  __/"
    echo "   /____/\____/_/\__,_/\___/\___/"
    echo -e "${RST}"
    echo ""
}

section_title() {
    local title="$1"
    local len=${#title}
    local total=46
    local left=$(( (total - len) / 2 ))
    [ "$left" -lt 0 ] && left=0
    printf -v pad "%*s" "$left" ""
    echo -e "${BLU}=============================================="
    echo -e "${BLU}${pad}${title}${RST}"
    echo -e "${BLU}=============================================="
    echo ""
}

# ─── STATUS HELPERS ───────────────────────────────────────

get_apphost_pid() {
    [ -f "$STARTUP_JSON" ] || return
    sed -n 's/.*"appHostPid": *\([0-9][0-9]*\).*/\1/p' "$STARTUP_JSON" | tail -n1
}

get_cli_pid() {
    [ -f "$STARTUP_JSON" ] || return
    sed -n 's/.*"cliPid": *\([0-9][0-9]*\).*/\1/p' "$STARTUP_JSON" | tail -n1
}

get_dashboard_url() {
    local url="" log
    if [ -f "$STARTUP_JSON" ]; then
        url="$(sed -n 's/.*"dashboardUrl": *"\([^"]*\)".*/\1/p' "$STARTUP_JSON" | tail -n1 | sed 's/\\u0026/\&/g; s/\\//g')"
    fi

    # The CLI keeps the detached startup JSON buffered, so on a running server it can
    # still be empty. Fall back to the AppHost log, which always prints the login URL.
    if [ -z "$url" ]; then
        log="$(ls -t "$UBUNTU_HOME"/.aspire/logs/cli_*detach-child*.log 2>/dev/null | head -n1)"
        if [ -n "$log" ] && [ -f "$log" ]; then
            url="$(grep -a -o 'http://localhost:[0-9][0-9]*/login?t=[a-f0-9]*' "$log" 2>/dev/null | tail -n1)"
        fi
    fi

    [ -n "$url" ] && printf '%s\n' "$url"
}

# The Aspire dashboard listens on a random loopback port that changes every time the
# AppHost starts. socat keeps it reachable at a stable LAN port; the login token itself
# still rotates and is printed by `earth status`.
get_public_dashboard_url() {
    local url
    url="$(get_dashboard_url)"
    [ -n "$url" ] || return
    printf '%s\n' "$url" | sed "s|^http://[^/]*|http://$PUBLIC_IP:$DASHBOARD_PORT|"
}

start_dashboard_forwarder() {
    command -v socat >/dev/null 2>&1 || {
        echo "[Solace] socat is not installed; the dashboard stays loopback-only."
        echo "[Solace] Install it with: pkg install socat"
        return 0
    }

    if [ -f "$DASHBOARD_FORWARD_PID" ]; then
        local old
        old="$(cat "$DASHBOARD_FORWARD_PID" 2>/dev/null)"
        [ -n "$old" ] && kill -0 "$old" 2>/dev/null && return 0
    fi

    nohup bash "$SELF_PATH" __dashboard-forward >/dev/null 2>&1 &
    echo "$!" > "$DASHBOARD_FORWARD_PID"
    disown 2>/dev/null || true
}

stop_dashboard_forwarder() {
    local pid
    if [ -f "$DASHBOARD_FORWARD_PID" ]; then
        pid="$(cat "$DASHBOARD_FORWARD_PID" 2>/dev/null)"
        [ -n "$pid" ] && kill "$pid" 2>/dev/null || true
        rm -f "$DASHBOARD_FORWARD_PID"
    fi
    if [ -f "$DASHBOARD_SOCAT_PID" ]; then
        pid="$(cat "$DASHBOARD_SOCAT_PID" 2>/dev/null)"
        [ -n "$pid" ] && kill "$pid" 2>/dev/null || true
        rm -f "$DASHBOARD_SOCAT_PID"
    fi
}

dashboard_forward_loop() {
    local url port last_port="" socat_pid=""

    while is_process_alive; do
        url="$(get_dashboard_url)"
        port="$(printf '%s' "$url" | sed -n 's|^http://[^:/]*:\([0-9][0-9]*\)/.*|\1|p')"

        if [ -n "$port" ] && {
            [ "$port" != "$last_port" ] ||
            [ -z "$socat_pid" ] ||
            ! kill -0 "$socat_pid" 2>/dev/null;
        }; then
            if [ -n "$socat_pid" ]; then
                kill "$socat_pid" 2>/dev/null || true
                wait "$socat_pid" 2>/dev/null || true
            fi
            socat "TCP-LISTEN:$DASHBOARD_PORT,bind=0.0.0.0,reuseaddr,fork" \
                "TCP:127.0.0.1:$port" >/dev/null 2>&1 &
            socat_pid=$!
            echo "$socat_pid" > "$DASHBOARD_SOCAT_PID"
            last_port="$port"
        fi

        sleep 5
    done

    [ -n "$socat_pid" ] && kill "$socat_pid" 2>/dev/null || true
    rm -f "$DASHBOARD_SOCAT_PID"
}

get_cli_log() {
    [ -f "$STARTUP_JSON" ] || return
    sed -n 's/.*"logFile": *"\([^"]*\)".*/\1/p' "$STARTUP_JSON" | tail -n1 | sed 's/\\//g'
}

# Translate a path from inside the container to a Termux path.
host_path() {
    case "$1" in
        /root/*) printf '%s%s\n' "$UBUNTU_HOME" "${1#/root}" ;;
        /*)      printf '%s%s\n' "$PROOT_ROOTFS" "$1" ;;
        *)       printf '%s\n' "$1" ;;
    esac
}

is_process_alive() {
    local pid
    if [ -f "$PID_FILE" ]; then
        pid="$(cat "$PID_FILE" 2>/dev/null)"
        [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null && return 0
    fi
    pid="$(get_apphost_pid)"
    [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null && return 0
    # Match the built executable path, not the CLI command line (which contains the
    # project path and used to make a dead server look alive). The bracket keeps a
    # diagnostic shell command from matching itself.
    pgrep -x "Solace.AppHost" >/dev/null 2>&1 ||
        pgrep -f '[b]in/Debug/net11.0/Solace.AppHost' >/dev/null 2>&1
}

is_running() {
    curl -fsS --max-time 2 "http://127.0.0.1:5000/health" >/dev/null 2>&1 && return 0
    curl -fsS --max-time 2 "http://127.0.0.1:5000/" >/dev/null 2>&1 && return 0
    return 1
}

is_starting() {
    is_process_alive && ! is_running
}

# ─── ENVIRONMENT ──────────────────────────────────────────

load_env() {
    if [ ! -f "$ENV_FILE" ]; then
        echo "Solace is not configured. Run the installer again."
        sleep 3
        exit 1
    fi
    set -a
    . "$ENV_FILE"
    set +a

    PUBLIC_IP="${SOLACE_PUBLIC_IP:-127.0.0.1}"
    DASHBOARD_PORT="${SOLACE_DASHBOARD_PORT:-18888}"
    INSTALL_BRANCH="${SOLACE_BRANCH:-$GITHUB_BRANCH}"
    ADMIN_URL="http://$PUBLIC_IP:5000"
    if [ -f "$SETTINGS_FILE" ]; then
        local branch
        branch="$(grep -o '"branch": *"[^"]*"' "$SETTINGS_FILE" | cut -d'"' -f4)"
        [ -n "$branch" ] && INSTALL_BRANCH="$branch"
    fi
}

set_env_value() {
    local key="$1"
    local value="$2"
    local quoted tmp found=0 line
    quoted="$(printf '%q' "$value")"
    tmp="$CONFIG_DIR/termux.env.tmp"
    : > "$tmp"
    while IFS= read -r line || [ -n "$line" ]; do
        case "$line" in
            "$key="*)
                printf '%s=%s\n' "$key" "$quoted" >> "$tmp"
                found=1
                ;;
            *)
                printf '%s\n' "$line" >> "$tmp"
                ;;
        esac
    done < "$ENV_FILE"
    if [ "$found" -eq 0 ]; then
        printf '%s=%s\n' "$key" "$quoted" >> "$tmp"
    fi
    cat "$tmp" > "$ENV_FILE"
    rm -f "$tmp"
}

save_settings_value() {
    local key="$1"
    local value="$2"
    [ -f "$SETTINGS_FILE" ] || return
    sed -i "s|\"$key\": *\"[^\"]*\"|\"$key\": \"$value\"|" "$SETTINGS_FILE"
}

# ─── UBUNTU HELPERS ───────────────────────────────────────

open_url() {
    local url="$1"
    if command -v termux-open-url >/dev/null 2>&1; then
        termux-open-url "$url" >/dev/null 2>&1 && return 0
    fi
    if [ -x "$TERMUX_PREFIX/bin/termux-open-url" ]; then
        "$TERMUX_PREFIX/bin/termux-open-url" "$url" >/dev/null 2>&1 && return 0
    fi
    echo "Open in your browser: $url"
    sleep 3
}

get_version() {
    local version
    version="$(grep -o '"version": *"[^"]*"' "$SETTINGS_FILE" 2>/dev/null | cut -d'"' -f4)"
    if [ -n "$version" ]; then
        printf '%s\n' "$version"
        return 0
    fi
    ubuntu_run git -C /root/Solace rev-parse --short HEAD 2>/dev/null
}

require_rootfs() {
    if [ ! -x "$TERMUX_PREFIX/bin/proot-distro" ] || [ ! -d "$UBUNTU_HOME" ]; then
        echo "Solace is not installed yet. Run install.sh first."
        return 1
    fi
    if [ ! -f "$ENV_FILE" ]; then
        echo "Solace is not configured. Run install.sh again."
        return 1
    fi
    return 0
}

ubuntu_run() {
    proot-distro login ubuntu -- "$@"
}

ubuntu_dotnet() {
    proot-distro login ubuntu -- bash -c "$UBUNTU_DOTNET_PRELUDE"'
exec "$@"' _ "$@"
}

check_deps() {
    local missing="" cmd ubuntu_missing
    for cmd in fzf pgrep pkill curl pg_ctl pg_isready psql; do
        command -v "$cmd" >/dev/null 2>&1 || missing="$missing $cmd"
    done
    [ -f "$TERMUX_PGDATA/PG_VERSION" ] || missing="$missing postgres"
    ubuntu_missing="$(ubuntu_run bash -c "$UBUNTU_DOTNET_PRELUDE"'
for c in dotnet aspire java git; do
    command -v "$c" >/dev/null 2>&1 || echo "$c"
done' 2>/dev/null)"
    if [ -n "$missing$ubuntu_missing" ]; then
        echo "Missing dependencies:$missing $ubuntu_missing"
        return 1
    fi
    return 0
}

# ─── POSTGRESQL (Termux-native) ───────────────────────────

pg_ready() {
    pg_isready -q -h 127.0.0.1 -p 5432 2>/dev/null
}

start_postgres() {
    pg_ready && return 0
    if [ ! -f "$TERMUX_PGDATA/PG_VERSION" ]; then
        echo "PostgreSQL is not configured. Run install.sh again."
        return 1
    fi
    pg_ctl -D "$TERMUX_PGDATA" -l "$TERMUX_PGDATA/server.log" -w -t 120 start >/dev/null 2>&1 || true
    local waited=0
    while [ "$waited" -lt 120 ]; do
        pg_ready && return 0
        sleep 1
        waited=$((waited + 1))
    done
    echo "PostgreSQL failed to start. See $TERMUX_PGDATA/server.log"
    return 1
}

stop_postgres() {
    pg_ready || return 0
    pg_ctl -D "$TERMUX_PGDATA" -w -t 60 stop -m fast >/dev/null 2>&1 || true
}

with_postgres() {
    # Runs $1 with PostgreSQL online. If the database was not running, it is stopped
    # again afterwards. Maintenance payloads use $SOLACE_DB_PASSWORD from termux.env.
    local payload="$1" started=0 rc
    if ! pg_ready; then
        start_postgres || return 1
        started=1
    fi
    bash -c "$payload"
    rc=$?
    [ "$started" -eq 1 ] && stop_postgres
    return "$rc"
}

# ─── START / STOP ─────────────────────────────────────────

start_server() {
    if is_process_alive; then
        return 0
    fi

    if ! check_deps; then
        echo "Run the installer again or use Settings > Repair."
        return 1
    fi

    start_postgres || return 1

    mkdir -p "$LOG_DIR"
    rm -f "$STARTUP_JSON" "$PID_FILE"
    : > "$STARTUP_LOG"

    # The wrapper stays alive while the AppHost runs. This keeps the proot session (and
    # therefore the server and PostgreSQL) alive after the TUI exits.
    nohup proot-distro login ubuntu -- env \
        ENV_FILE="$UBUNTU_ENV_FILE" \
        STARTUP_JSON="$UBUNTU_STARTUP_JSON" \
        APP_HOST="$UBUNTU_APP_HOST" \
        bash -c '
            set -a
            . "$ENV_FILE"
            set +a
            export DOTNET_ROOT="$HOME/.dotnet"
            export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
            export DOTNET_CLI_TELEMETRY_OPTOUT=1
            export DOTNET_NOLOGO=1
            export DOTNET_gcServer=0
            export ASPIRE_CLI_START_TIMEOUT=21600
            cd /root/Solace || exit 1
            aspire run --detach --format json --non-interactive --apphost "$APP_HOST" -- --environment Termux >"$STARTUP_JSON"
            rc=$?
            [ "$rc" -eq 0 ] || exit "$rc"
            pid="$(sed -n "s/.*\"appHostPid\": *\([0-9][0-9]*\).*/\1/p" "$STARTUP_JSON" | tail -n1)"
            [ -n "$pid" ] || exit 1
            while kill -0 "$pid" 2>/dev/null; do
                sleep 5
            done
            exit 0
        ' >>"$STARTUP_LOG" 2>&1 </dev/null &

    echo "$!" > "$PID_FILE"
    disown 2>/dev/null || true

    local waited=0
    while [ "$waited" -lt 10 ]; do
        [ -s "$STARTUP_JSON" ] && break
        if ! kill -0 "$(cat "$PID_FILE" 2>/dev/null)" 2>/dev/null && ! is_process_alive; then
            echo ""
            echo -e "${RED}Solace failed to start.${RST}"
            echo ""
            tail -n 40 "$STARTUP_LOG" 2>/dev/null || true
            return 1
        fi
        sleep 1
        waited=$((waited + 1))
    done

    echo "Solace is starting. The first start can take several minutes."
    start_dashboard_forwarder
    return 0
}

stop_server() {
    stop_dashboard_forwarder

    if [ -f "$SOLACE_DIR/src/Solace.AppHost/Solace.AppHost.csproj" ]; then
        ubuntu_dotnet aspire stop --non-interactive --apphost "$UBUNTU_APP_HOST" >/dev/null 2>&1 &
        local stopper=$!
        local waited=0
        while kill -0 "$stopper" 2>/dev/null && [ "$waited" -lt 60 ]; do
            sleep 1
            waited=$((waited + 1))
        done
        kill "$stopper" 2>/dev/null || true
    fi

    local pid
    pid="$(get_apphost_pid)"
    [ -n "$pid" ] && kill "$pid" 2>/dev/null || true
    pid="$(get_cli_pid)"
    [ -n "$pid" ] && kill "$pid" 2>/dev/null || true
    if [ -f "$PID_FILE" ]; then
        pid="$(cat "$PID_FILE" 2>/dev/null)"
        [ -n "$pid" ] && kill "$pid" 2>/dev/null || true
    fi

    sleep 2
    pkill -f "Solace\.AppHost|Solace\.WebPortal|Solace\.ApiServer|Solace\.AuthServer|Solace\.Cdn|Solace\.Locator" 2>/dev/null || true
    pkill -f "aspire run .*Solace" 2>/dev/null || true
    pkill -f "proot.*aspire stop" 2>/dev/null || true

    local waited=0
    while is_process_alive && [ "$waited" -lt 30 ]; do
        sleep 1
        waited=$((waited + 1))
    done

    stop_postgres
    rm -f "$PID_FILE" "$STARTUP_JSON"
}

# ─── EULA ─────────────────────────────────────────────────

eula_confirmation_loop() {
    load_env
    [ "${Shared__AcceptMinecraftEula:-false}" = "true" ] && return 0

    while true; do
        clear; show_banner
        section_title "EULA CONFIRMATION"
        echo "  Before starting the server, you must accept the"
        echo "  Minecraft End User License Agreement."
        echo ""
        echo "  Read it here:"
        echo "  https://aka.ms/MinecraftEULA"
        echo ""
        CHOICE=$(printf "Yes, I agree\nNo, I deny" | fzf \
            --height=20% --reverse --border --prompt="Accept EULA? > ")

        if [ "$CHOICE" = "Yes, I agree" ]; then
            set_env_value "Shared__AcceptMinecraftEula" "true"
            echo ""
            echo "[Solace] EULA accepted."
            sleep 1
            return 0
        fi

        clear; show_banner
        section_title "ARE YOU SURE?"
        echo "  The server cannot run buildplate services without"
        echo "  accepting the EULA."
        echo ""
        CHOICE2=$(printf "Go back\nYes, I deny" | fzf \
            --height=20% --reverse --border --prompt="Confirm? > ")
        [ "$CHOICE2" = "Yes, I deny" ] && return 1
    done
}

# ─── FIRST START CHECKS ───────────────────────────────────

first_start_checks() {
    load_env

    if [ ! -d "$SOLACE_DIR/.git" ]; then
        clear; show_banner
        section_title "INSTALLATION NOT FOUND"
        echo "  $UBUNTU_SOLACE_DIR is missing inside Ubuntu."
        echo "  Run the installer again."
        printf "Press ENTER..."
        pause
        return 1
    fi

    if [ ! -f "$SOLACE_DIR/staticdata/resourcepacks/vanilla.zip" ]; then
        clear; show_banner
        section_title "RESOURCE PACK REQUIRED"
        echo "  The vanilla resource pack is missing:"
        echo "  $UBUNTU_SOLACE_DIR/staticdata/resourcepacks/vanilla.zip"
        echo ""
        echo "  Without it, textures, the store and buildplate"
        echo "  previews will not work correctly."
        echo ""
        CHOICE=$(printf "Continue anyway\nCancel" | fzf \
            --height=20% --reverse --border --prompt="Resource pack > ")
        [ "$CHOICE" = "Continue anyway" ] || return 1
    fi

    eula_confirmation_loop || return 1
    return 0
}

# ─── LOGS ─────────────────────────────────────────────────

read_password_from_log() {
    local log="$1"
    [ -n "$log" ] && [ -f "$log" ] || return 1
    grep -a -A6 "SETUP: Initial owner account created" "$log" 2>/dev/null \
        | sed 's/\x1b\[[0-9;]*m//g' \
        | tr -d '\r' \
        | sed -n 's/.*Password: *//p' \
        | tail -n1
}

get_admin_password() {
    local log password f
    local paths=""

    log="$(get_cli_log)"
    [ -n "$log" ] && paths="$(host_path "$log")"
    for log in $paths "$STARTUP_LOG"; do
        password="$(read_password_from_log "$log")"
        if [ -n "$password" ]; then
            printf '%s\n' "$password"
            return 0
        fi
    done

    # DCP stores each resource's stdout in /tmp/aspire-dcp*/<id>_out. The initial
    # password is only printed once (when the database is first created), so this is
    # the most reliable place to recover it from.
    for f in $(ls -t "$PROOT_ROOTFS"/tmp/aspire-dcp*/*_out 2>/dev/null); do
        password="$(read_password_from_log "$f")"
        if [ -n "$password" ]; then
            printf '%s\n' "$password"
            return 0
        fi
    done

    # Resource logs are not written to the CLI log, so ask the Aspire backchannel.
    if is_process_alive; then
        password="$(ubuntu_dotnet timeout 60 aspire logs web-portal --apphost "$UBUNTU_APP_HOST" --tail 5000 2>/dev/null \
            | sed -n 's/.*Password: *//p' \
            | tail -n1)"
        if [ -n "$password" ]; then
            printf '%s\n' "$password"
            return 0
        fi
    fi

    return 1
}

show_resource_logs() {
    local resource="$1"
    clear; show_banner; section_title "LOGS - $resource"
    ubuntu_dotnet timeout 60 aspire logs "$resource" --apphost "$UBUNTU_APP_HOST" --tail 200 2>/dev/null || echo "  No logs available."
    echo ""
    printf "Press ENTER..."
    pause
}

logs_menu() {
    while true; do
        clear; show_banner
        section_title "LOGS"

        local log_file="$STARTUP_LOG"
        local cli_log
        cli_log="$(get_cli_log)"
        if [ -n "$cli_log" ] && [ -f "$(host_path "$cli_log")" ]; then
            log_file="$(host_path "$cli_log")"
        fi

        if [ -f "$log_file" ]; then
            echo -e "  ${CYN}$log_file${RST}"
            echo ""
            tail -n 100 "$log_file"
        else
            echo "  No log file yet."
        fi
        echo ""

        local choice
        choice=$(printf "Refresh\nService logs\nStartup output\nBack" | fzf \
            --height=15% --reverse --border --prompt="Logs > ")
        case "$choice" in
            "Service logs")
                local service
                service=$(printf "web-portal\napi-server\nauth-server\nlocator\ncdn\nevent-bus\nobject-store" | fzf \
                    --height=30% --reverse --border --prompt="Service > ")
                [ -n "$service" ] && show_resource_logs "$service"
                ;;
            "Startup output")
                clear; show_banner; section_title "STARTUP OUTPUT"
                if [ -f "$STARTUP_LOG" ]; then
                    tail -n 150 "$STARTUP_LOG"
                else
                    echo "  No startup output yet."
                fi
                printf "Press ENTER..."
                pause
                ;;
            "Back"|"") return ;;
        esac
    done
}

# ─── PROCESS VIEWER ───────────────────────────────────────

process_viewer() {
    while true; do
        clear; show_banner
        section_title "PROCESS EXPLORER"
        if is_running; then
            echo -e "  ${GRN}[RUNNING]${RST}  $ADMIN_URL"
        elif is_process_alive; then
            echo -e "  ${YLW}[STARTING]${RST} $ADMIN_URL"
        else
            echo -e "  ${RED}[STOPPED]${RST}"
        fi
        echo ""

        local selected
        selected=$({
            echo "Back to Main Menu"
            ps -eo pid,ppid,cmd --no-headers 2>/dev/null | \
                grep -E "aspire|Solace|dotnet|java" | \
                grep -v "grep -E"
        } | fzf --height=50% --reverse --border --prompt="Process > ")

        [ -z "$selected" ] && continue
        [ "$selected" = "Back to Main Menu" ] && return

        local selpid
        selpid=$(echo "$selected" | awk '{print $1}')
        [[ "$selpid" =~ ^[0-9]+$ ]] || continue

        while true; do
            clear
            echo "==== PROCESS ===="
            echo "PID: $selpid"
            echo ""
            ps -p "$selpid" -o pid,ppid,etime,cmd 2>/dev/null || echo "Process exited."
            echo ""
            local log_file="$STARTUP_LOG"
            local cli_log
            cli_log="$(get_cli_log)"
            if [ -n "$cli_log" ] && [ -f "$(host_path "$cli_log")" ]; then
                log_file="$(host_path "$cli_log")"
            fi
            [ -f "$log_file" ] && tail -n 80 "$log_file"
            local ch
            ch=$(printf "Refresh\nBack" | fzf --height=10% --reverse --border --prompt="Process > ")
            [ "$ch" = "Back" ] && break
        done
    done
}

# ─── UPDATE ───────────────────────────────────────────────

update_solace() {
    load_env
    stop_server

    clear; show_banner
    section_title "UPDATE SOLACE"
    echo "  Branch: $INSTALL_BRANCH"
    echo ""

    if ! proot-distro login ubuntu -- env BRANCH="$INSTALL_BRANCH" bash -s <<'INNER'
set -e
cd /root/Solace
git fetch --prune origin "$BRANCH"
git checkout "$BRANCH"
git pull --ff-only origin "$BRANCH"
git submodule update --init --recursive
INNER
    then
        echo -e "${RED}Update failed. You may have local changes in $UBUNTU_SOLACE_DIR.${RST}"
        printf "Press ENTER..."
        pause
        return 1
    fi

    local version
    version="$(ubuntu_run git -C "$UBUNTU_SOLACE_DIR" rev-parse --short HEAD 2>/dev/null)"
    save_settings_value "version" "$version"
    save_settings_value "updatedAt" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"

    echo ""
    echo -e "${GRN}[Solace] Updated to $version.${RST}"
    echo "The next start will rebuild the projects."
    printf "Press ENTER..."
    pause
}

# ─── SETTINGS ─────────────────────────────────────────────

change_public_ip() {
    load_env
    clear; show_banner
    section_title "PUBLIC ENDPOINT"
    echo "  Use 127.0.0.1 for playing on this device."
    echo "  Use your phone's LAN IP for other devices."
    echo ""
    printf "IP or hostname [%s] > " "$PUBLIC_IP"
    read -r NEW_IP < /dev/tty
    NEW_IP="$(echo "$NEW_IP" | tr -d '\r\n')"
    [ -z "$NEW_IP" ] && return

    set_env_value "SOLACE_PUBLIC_IP" "$NEW_IP"
    for name in WebPortal Locator AuthServer ApiServer Cdn; do
        case "$name" in
            WebPortal) port=5000 ;;
            Locator) port=8080 ;;
            AuthServer) port=8088 ;;
            ApiServer) port=8089 ;;
            Cdn) port=8090 ;;
        esac
        set_env_value "Shared__PublicEndpoints__$name" "http://$NEW_IP:$port"
    done
    set_env_value "BuildplateLauncher__PublicEndpoint" "$NEW_IP"
    echo ""
    echo "[Solace] Endpoint updated. Restart the server."
    sleep 2
}

show_admin_credentials() {
    load_env
    clear; show_banner
    section_title "ADMIN ACCOUNT"
    echo "  Email:    admin@solace.com"
    local password
    password="$(get_admin_password)"
    if [ -n "$password" ]; then
        echo "  Password: $password"
    else
        echo "  Password: not found yet"
        echo ""
        echo "  Start the server once, then check this screen again."
        echo "  If the server has already started, open Logs to read"
        echo "  the Web Portal output."
    fi
    echo ""
    echo "  Change it after your first login."
    printf "Press ENTER..."
    pause
}

switch_branch() {
    load_env
    clear; show_banner
    section_title "SWITCH BRANCH"
    echo "  Current: $INSTALL_BRANCH"
    echo ""
    echo -e "  ${YLW}Only the aspire branch currently has Termux support.${RST}"
    echo ""
    printf "Branch [%s] > " "$INSTALL_BRANCH"
    read -r NEW_BRANCH < /dev/tty
    NEW_BRANCH="$(echo "$NEW_BRANCH" | tr -d '\r\n')"
    [ -z "$NEW_BRANCH" ] && return
    [ "$NEW_BRANCH" = "$INSTALL_BRANCH" ] && return

    if [ "$NEW_BRANCH" != "aspire" ]; then
        printf "This branch may not support Termux. Continue? [y/N] > "
        read -r CONFIRM < /dev/tty
        case "$CONFIRM" in
            y|Y|yes|YES) ;;
            *) return ;;
        esac
    fi

    set_env_value "SOLACE_BRANCH" "$NEW_BRANCH"
    INSTALL_BRANCH="$NEW_BRANCH"
    save_settings_value "branch" "$NEW_BRANCH"
    update_solace
}

reset_databases() {
    load_env
    clear; show_banner
    section_title "RESET DATABASES"
    echo "  This deletes all Solace data: profiles, buildplates,"
    echo "  accounts and the web portal database."
    echo ""
    printf "Type RESET to continue > "
    read -r CONFIRM < /dev/tty
    [ "$CONFIRM" = "RESET" ] || return
    stop_server

    with_postgres 'for db in EarthDb PlayfabDb WebPortalDb; do
        PGPASSWORD="$SOLACE_DB_PASSWORD" dropdb -h 127.0.0.1 -U solace --force --if-exists "$db" 2>/dev/null || true
        PGPASSWORD="$SOLACE_DB_PASSWORD" createdb -h 127.0.0.1 -U solace -O solace "$db" 2>/dev/null || true
    done'
    rm -rf "$UBUNTU_HOME/.aspnet/DataProtection-Keys"

    echo "[Solace] Databases reset."
    sleep 2
}

uninstall_solace() {
    clear; show_banner
    section_title "UNINSTALL"
    echo "  This permanently removes Solace files, configuration"
    echo "  and databases."
    echo ""
    printf "Type YES to continue > "
    read -r CONFIRM < /dev/tty
    [ "$CONFIRM" = "YES" ] || return

    stop_server

    if [ -f "$TERMUX_PGDATA/.solace-managed" ]; then
        # The cluster was created by the installer, so remove it completely.
        stop_postgres
        rm -rf "$TERMUX_PGDATA"
    else
        # Keep a cluster the user created themselves; just drop the Solace objects.
        if [ -f "$ENV_FILE" ]; then
            set -a
            . "$ENV_FILE"
            set +a
        fi
        with_postgres 'for db in EarthDb PlayfabDb WebPortalDb; do
            PGPASSWORD="$SOLACE_DB_PASSWORD" dropdb -h 127.0.0.1 -U solace --force --if-exists "$db" 2>/dev/null || true
        done
        PGPASSWORD="$SOLACE_DB_PASSWORD" psql -h 127.0.0.1 -U solace -d postgres -c "DROP ROLE IF EXISTS solace" 2>/dev/null || true'
    fi

    rm -rf "$SOLACE_DIR" "$CONFIG_DIR" "$UBUNTU_HOME/.aspnet/DataProtection-Keys"
    if [ -n "${SOLACE_LAUNCHER:-}" ]; then
        rm -f "$SOLACE_LAUNCHER" 2>/dev/null || true
        echo "[Solace] Uninstalled, including the earth command."
    else
        echo "[Solace] Uninstalled."
        echo "The launcher was not installed through the Termux installer."
    fi
    echo "[Solace] The Termux postgresql package was left installed."
    sleep 3
    exit 0
}

settings_menu() {
    while true; do
        load_env
        clear; show_banner
        section_title "SETTINGS"
        echo -e "  ${CYN}Endpoint:${RST} $PUBLIC_IP"
        echo -e "  ${CYN}Branch:${RST}   $INSTALL_BRANCH"
        echo -e "  ${CYN}Server:${RST}   $UBUNTU_SOLACE_DIR"
        echo ""

        local choice
        choice=$(printf "Change Public IP\nShow Admin Account\nSwitch Branch\nReset Databases\nUninstall\nBack" | fzf \
            --height=30% --reverse --border --prompt="Settings > " --no-multi)
        case "$choice" in
            "Change Public IP") change_public_ip ;;
            "Show Admin Account") show_admin_credentials ;;
            "Switch Branch") switch_branch ;;
            "Reset Databases") reset_databases ;;
            "Uninstall") uninstall_solace ;;
            "Back"|"") return ;;
        esac
    done
}

# ─── INFORMATION ──────────────────────────────────────────

info_panel() {
    load_env
    clear; show_banner
    section_title "INFORMATION"
    echo "  ── Endpoints ──"
    echo "  Admin Panel:      http://$PUBLIC_IP:5000"
    echo "  Locator:          http://$PUBLIC_IP:8080"
    echo "  Auth Server:      http://$PUBLIC_IP:8088"
    echo "  API Server:       http://$PUBLIC_IP:8089"
    echo "  CDN:              http://$PUBLIC_IP:8090"
    echo "  Buildplate:       $PUBLIC_IP:19132+"
    echo ""
    local dashboard
    dashboard="$(get_public_dashboard_url)"
    [ -z "$dashboard" ] && dashboard="$(get_dashboard_url)"
    if [ -n "$dashboard" ]; then
        echo "  Aspire Dashboard: $dashboard"
    else
        echo "  Aspire Dashboard: available after the server starts"
    fi
    echo ""
    echo "  ── Setup Steps ──"
    echo "  1. Start the server from the main menu"
    echo "  2. Open the Admin Panel: http://$PUBLIC_IP:5000"
    echo "  3. Log in as admin@solace.com (password: Settings > Show Admin Account)"
    echo "  4. Create a separate account for in-game sign-in"
    echo "  5. Patch Minecraft Earth and use the endpoints above"
    echo ""
    echo "  Resource pack:"
    echo "    $UBUNTU_SOLACE_DIR/staticdata/resourcepacks/vanilla.zip"
    echo ""
    echo "  Object store:"
    echo "    $UBUNTU_SOLACE_DIR/data/object_store"
    echo ""
    echo "  Version:"
    echo "    $(get_version) ($INSTALL_BRANCH)"
    echo ""
    printf "Back\n" | fzf --height=12% --reverse --border --prompt="Info > " >/dev/null
}

# ─── COMMAND LINE ─────────────────────────────────────────

case "${1:-}" in
    help|--help|-h) update_self "$@"; show_help ;;
esac

if [ "${1:-}" = "eula" ]; then
    EULA_ACTION="${2:-}"
    if [ ! -f "$ENV_FILE" ]; then
        echo "[Solace] Not installed. Run the installer first."
        exit 1
    fi
    if [ "$EULA_ACTION" = "--delete" ]; then
        set_env_value "Shared__AcceptMinecraftEula" "false"
        echo "[Solace] Minecraft EULA acceptance removed."
        exit 0
    fi
    set_env_value "Shared__AcceptMinecraftEula" "true"
    echo "[Solace] Minecraft EULA accepted."
    echo "[Solace] Restart the server for this to take effect."
    exit 0
fi

if [ "${1:-}" = "start" ]; then
    load_env
    if [ "${Shared__AcceptMinecraftEula:-false}" != "true" ]; then
        echo "[Solace] Accept the Minecraft EULA first: earth eula"
        exit 1
    fi
    if is_process_alive; then
        start_dashboard_forwarder
        echo "[Solace] Already running."
        exit 0
    fi
    start_server || exit 1
    echo "[Solace] The server is starting. Check it with: earth status"
    exit 0
fi

if [ "${1:-}" = "stop" ]; then
    load_env
    stop_server
    echo "[Solace] Stopped."
    exit 0
fi

if [ "${1:-}" = "status" ]; then
    load_env
    if is_running; then
        echo "[Solace] RUNNING  $ADMIN_URL"
        dashboard="$(get_public_dashboard_url)"
        [ -n "$dashboard" ] || dashboard="$(get_dashboard_url)"
        [ -n "$dashboard" ] && echo "[Solace] Dashboard: $dashboard"
        exit 0
    fi
    if is_process_alive; then
        echo "[Solace] STARTING $ADMIN_URL"
        dashboard="$(get_public_dashboard_url)"
        [ -n "$dashboard" ] || dashboard="$(get_dashboard_url)"
        [ -n "$dashboard" ] && echo "[Solace] Dashboard: $dashboard"
        exit 0
    fi
    echo "[Solace] STOPPED"
    exit 1
fi

if [ "${1:-}" = "logs" ]; then
    load_env
    if [ -n "${2:-}" ]; then
        ubuntu_dotnet timeout 60 aspire logs "$2" --apphost "$UBUNTU_APP_HOST" --tail 200
        exit 0
    fi
    log_file="$STARTUP_LOG"
    cli_log="$(get_cli_log)"
    if [ -n "$cli_log" ] && [ -f "$(host_path "$cli_log")" ]; then
        log_file="$(host_path "$cli_log")"
    fi
    tail -n 200 "$log_file" 2>/dev/null || echo "[Solace] No logs yet."
    exit 0
fi

if [ "${1:-}" = "uninstall" ]; then
    SOLACE_LAUNCHER="$LAUNCHER_PATH"
    uninstall_solace
    exit 0
fi

# ─── MAIN DASHBOARD ───────────────────────────────────────

# Internal worker: keeps the fixed LAN dashboard port pointed at the dashboard's
# current random loopback port. Started in the background by start_server.
if [ "${1:-}" = "__dashboard-forward" ]; then
    load_env
    dashboard_forward_loop
    exit 0
fi

update_self "$@"
require_rootfs || { echo "Run install.sh first."; exit 1; }
load_env

while true; do
    printf '\033[H\033[J'
    tput civis 2>/dev/null || true
    show_banner

    if is_running; then
        echo -e "  ${GRN}●${RST} ${GRN}[RUNNING]${RST}  |  $ADMIN_URL"
    elif is_starting; then
        echo -e "  ${YLW}●${RST} ${YLW}[STARTING]${RST}  |  $ADMIN_URL"
    else
        echo -e "  ${RED}●${RST} ${RED}[STOPPED]${RST}  |  $ADMIN_URL"
    fi
    echo -e "  ─────────────────────────────────────────────"
    echo ""
    echo -e "  ┌─────────────────────────────────────────────┐"
    echo -e "  │ ${CYN}[1]${RST} Start/Stop Server                       │"
    echo -e "  │ ${CYN}[2]${RST} Open Aspire Dashboard                   │"
    echo -e "  │ ${CYN}[3]${RST} Open Admin Panel                        │"
    echo -e "  │ ${CYN}[4]${RST} Logs                                    │"
    echo -e "  │ ${CYN}[5]${RST} Process Explorer                        │"
    echo -e "  │ ${CYN}[6]${RST} Update Solace                           │"
    echo -e "  │ ${CYN}[7]${RST} Settings                                │"
    echo -e "  │ ${CYN}[8]${RST} Information                             │"
    echo -e "  │ ${CYN}[0]${RST} Exit                                    │"
    echo -e "  └─────────────────────────────────────────────┘"
    echo -e "  ${YLW}Solace Aspire (Termux - $INSTALL_BRANCH)${RST}"

    read -t 2 -n 1 KEY < /dev/tty || true

    case "$KEY" in
        1)
            if is_process_alive; then
                local_choice=$(printf "Yes\nNo" | fzf \
                    --height=20% --reverse --border --prompt="Stop server? > ")
                [ "$local_choice" = "Yes" ] && stop_server
            else
                first_start_checks && start_server
            fi
            ;;
        2)
            DASH_URL="$(get_public_dashboard_url)"
            [ -z "$DASH_URL" ] && DASH_URL="$(get_dashboard_url)"
            if [ -n "$DASH_URL" ]; then
                open_url "$DASH_URL"
            else
                clear; show_banner; section_title "ASPIRE DASHBOARD"
                echo "  The dashboard URL is not available yet."
                echo "  Start the server and try again."
                printf "Press ENTER..."
                pause
            fi
            ;;
        3) open_url "$ADMIN_URL" ;;
        4) logs_menu ;;
        5) process_viewer ;;
        6) update_solace ;;
        7) settings_menu ;;
        8) info_panel ;;
        0|q)
            if is_process_alive; then
                clear; show_banner
                section_title "SERVER IS RUNNING"
                echo "  Exiting the TUI does not stop the server."
                echo "  Use option 1 to stop it."
                echo ""
                EXIT_CHOICE=$(printf "Cancel\nExit TUI" | fzf \
                    --height=20% --reverse --border --prompt="Exit? > ")
                [ "$EXIT_CHOICE" = "Exit TUI" ] || continue
            fi
            tput cnorm 2>/dev/null || true
            clear
            break
            ;;
    esac
done
