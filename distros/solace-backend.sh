#!/data/data/com.termux/files/usr/bin/bash

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
ORG='\033[38;5;208m'
CYN='\033[1;36m'
BLU='\033[1;34m'
RST='\033[0m'

GITHUB_REPO="${SOLACE_REPO:-cosmetide/Solace}"
GITHUB_BRANCH="${SOLACE_BRANCH:-main}"
SELF_PATH="$(realpath "$0" 2>/dev/null || echo "$0")"
TERMUX_PREFIX="${PREFIX:-/data/data/com.termux/files/usr}"

PROOT_ROOTFS="$TERMUX_PREFIX/var/lib/proot-distro/containers/ubuntu/rootfs"
UBUNTU_HOME="$PROOT_ROOTFS/root"
SOLACE_DIR="$UBUNTU_HOME/Solace"
CONFIG_DIR="$UBUNTU_HOME/.config/solace"
ENV_FILE="$CONFIG_DIR/termux.env"
if [ -f "$ENV_FILE" ]; then
    ENV_REPO="$(sed -n 's/^SOLACE_REPO=//p' "$ENV_FILE" | tail -n1 | tr -d "'\"")"
    ENV_BRANCH="$(sed -n 's/^SOLACE_BRANCH=//p' "$ENV_FILE" | tail -n1 | tr -d "'\"")"
    [ -z "${SOLACE_REPO:-}" ] && [ -n "$ENV_REPO" ] && GITHUB_REPO="$ENV_REPO"
    [ -z "${SOLACE_BRANCH:-}" ] && [ -n "$ENV_BRANCH" ] && GITHUB_BRANCH="$ENV_BRANCH"
fi
SETTINGS_FILE="$CONFIG_DIR/termux-settings.json"
LOG_DIR="$CONFIG_DIR/logs"
STARTUP_JSON="$LOG_DIR/startup.json"
STARTUP_LOG="$LOG_DIR/startup.log"
PID_FILE="$CONFIG_DIR/server.pid"
DASHBOARD_PORT="${SOLACE_DASHBOARD_PORT:-8888}"
DASHBOARD_FORWARD_PID="$CONFIG_DIR/dashboard-forward.pid"
DASHBOARD_SOCAT_PID="$CONFIG_DIR/dashboard-socat.pid"
ADMIN_URL="http://127.0.0.1:5000"
LAUNCHER_PORT="${SOLACE_LAUNCHER_PORT:-7089}"

TERMUX_PGDATA="$TERMUX_PREFIX/var/lib/postgresql"

UBUNTU_SOLACE_DIR="/root/Solace"
UBUNTU_CONFIG_DIR="/root/.config/solace"
UBUNTU_ENV_FILE="/root/.config/solace/termux.env"
UBUNTU_STARTUP_JSON="/root/.config/solace/logs/startup.json"
UBUNTU_APP_HOST="/root/Solace/src/Solace.AppHost/Solace.AppHost.csproj"
UBUNTU_DOTNET_PRELUDE='export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1'

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

    if [ -z "$url" ]; then
        log="$(ls -t "$UBUNTU_HOME"/.aspire/logs/cli_*detach-child*.log 2>/dev/null | head -n1)"
        if [ -n "$log" ] && [ -f "$log" ]; then
            url="$(grep -a -o 'http://localhost:[0-9][0-9]*/login?t=[a-f0-9]*' "$log" 2>/dev/null | tail -n1)"
        fi
    fi

    [ -n "$url" ] && printf '%s\n' "$url"
}

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
    pgrep -x "Solace.AppHost" >/dev/null 2>&1 ||
        pgrep -f '[b]in/Debug/net11.0/Solace.AppHost' >/dev/null 2>&1
}

is_running() {
    curl -fsS --max-time 2 "http://127.0.0.1:5000/health" >/dev/null 2>&1 && return 0
    curl -fsS --max-time 2 "http://127.0.0.1:5000/" >/dev/null 2>&1 && return 0
    return 1
}

ensure_prebuilt_compat() {
    # Older prebuilt AppHost binaries contain the GitHub Actions workspace path.
    # The install lives at /root/Solace inside Ubuntu, so provide a compatibility link.
    [ -f "$SOLACE_DIR/.termux-prebuilt" ] || return 0
    local legacy="$PROOT_ROOTFS/home/runner/work/Solace/Solace"
    if [ ! -e "$legacy" ] && [ ! -L "$legacy" ]; then
        mkdir -p "$(dirname "$legacy")" 2>/dev/null || return 0
        ln -s /root/Solace "$legacy" 2>/dev/null || true
    fi
}

json_escape() {
    printf '%s' "$1" | tr -d '\r\n' | sed 's/\\/\\\\/g; s/"/\\"/g'
}

read_version() {
    local version
    version="$(grep -o '"version": *"[^"]*"' "$SETTINGS_FILE" 2>/dev/null | cut -d'"' -f4)"
    if [ -z "$version" ] && [ -d "$SOLACE_DIR/.git" ]; then
        version="$(git -C "$SOLACE_DIR" rev-parse --short HEAD 2>/dev/null || true)"
    fi
    printf '%s\n' "$version"
}

status_json() {
    local state running starting eula resource mode version branch dashboard services
    load_env

    if is_running; then
        state="running"
        running="true"
        starting="false"
    elif is_process_alive; then
        state="starting"
        running="false"
        starting="true"
    else
        state="stopped"
        running="false"
        starting="false"
    fi

    [ "${Shared__AcceptMinecraftEula:-false}" = "true" ] && eula="true" || eula="false"
    [ -f "$SOLACE_DIR/staticdata/resourcepacks/vanilla.zip" ] && resource="true" || resource="false"
    [ -f "$SOLACE_DIR/.termux-prebuilt" ] && mode="prebuilt" || mode="source"
    version="$(read_version)"
    branch="$INSTALL_BRANCH"
    dashboard=""
    if [ "$state" != "stopped" ]; then
        dashboard="$(get_public_dashboard_url 2>/dev/null || true)"
        [ -n "$dashboard" ] || dashboard="$(get_dashboard_url 2>/dev/null || true)"
    fi

    services="[{\"name\":\"Web Portal\",\"url\":\"http://$PUBLIC_IP:5000\"},{\"name\":\"Locator\",\"url\":\"http://$PUBLIC_IP:8080\"},{\"name\":\"Auth Server\",\"url\":\"http://$PUBLIC_IP:8088\"},{\"name\":\"API Server\",\"url\":\"http://$PUBLIC_IP:8089\"},{\"name\":\"CDN\",\"url\":\"http://$PUBLIC_IP:8090\"}]"

    printf '{"state":"%s","running":%s,"starting":%s,"eulaAccepted":%s,"resourcePack":%s,"installMode":"%s","version":"%s","branch":"%s","publicIp":"%s","portalUrl":"%s","dashboardUrl":"%s","adminEmail":"admin@solace.com","launcherPort":%s,"services":%s}\n' \
        "$(json_escape "$state")" "$running" "$starting" "$eula" "$resource" \
        "$(json_escape "$mode")" "$(json_escape "$version")" "$(json_escape "$branch")" \
        "$(json_escape "$PUBLIC_IP")" "$(json_escape "http://$PUBLIC_IP:5000")" \
        "$(json_escape "$dashboard")" "$LAUNCHER_PORT" "$services"
}

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
    DASHBOARD_PORT="${SOLACE_DASHBOARD_PORT:-8888}"
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

ubuntu_run() {
    proot-distro login ubuntu -- "$@"
}

ubuntu_dotnet() {
    proot-distro login ubuntu -- bash -c "$UBUNTU_DOTNET_PRELUDE"'
exec "$@"' _ "$@"
}

check_deps() {
    local missing="" cmd ubuntu_missing
    for cmd in pgrep pkill curl pg_ctl pg_isready psql; do
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

start_server() {
    ensure_prebuilt_compat
    if is_process_alive; then
        return 0
    fi

    if ! check_deps; then
        echo "Run the installer again."
        return 1
    fi

    start_postgres || return 1

    mkdir -p "$LOG_DIR"
    rm -f "$STARTUP_JSON" "$PID_FILE"
    : > "$STARTUP_LOG"

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
            ASPIRE_BUILD_ARGS=""
            if [ -f /root/Solace/.termux-prebuilt ]; then
                ASPIRE_BUILD_ARGS="--no-build"
                echo "[Solace] Using prebuilt Aspire bundle."
            fi
            aspire run --detach --format json --non-interactive $ASPIRE_BUILD_ARGS --apphost "$APP_HOST" -- --environment Termux >"$STARTUP_JSON"
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

    for f in $(ls -t "$PROOT_ROOTFS"/tmp/aspire-dcp*/*_out 2>/dev/null); do
        password="$(read_password_from_log "$f")"
        if [ -n "$password" ]; then
            printf '%s\n' "$password"
            return 0
        fi
    done

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

refresh_launcher_files() {
    local src="$SOLACE_DIR/distros"
    local dest="$TERMUX_PREFIX/libexec/solace"

    [ -f "$src/solace" ] || return 0
    mkdir -p "$dest/web/assets"
    install -m 755 "$src/solace" "$TERMUX_PREFIX/bin/solace"
    install -m 755 "$src/solace-backend.sh" "$dest/backend.sh"
    install -m 755 "$src/solace_launcher.py" "$dest/launcher.py"
    cp -a "$src/launcher/." "$dest/web/"
    if [ -f "$SOLACE_DIR/images/solace_branding_compact.png" ]; then
        cp "$SOLACE_DIR/images/solace_branding_compact.png" "$dest/web/assets/branding.png"
    fi
    if [ -f "$SOLACE_DIR/images/solace_banner.png" ]; then
        cp "$SOLACE_DIR/images/solace_banner.png" "$dest/web/assets/banner.png"
    fi
    rm -f "$TERMUX_PREFIX/bin/earth"
}

update_solace() {
    load_env
    echo "Updating Solace from $INSTALL_BRANCH..."
    stop_server

    if ! proot-distro login ubuntu -- env BRANCH="$INSTALL_BRANCH" bash -s <<'INNER'
set -e
cd /root/Solace
git fetch --prune origin "$BRANCH"
git checkout "$BRANCH"
git pull --ff-only origin "$BRANCH"
git submodule update --init --recursive
rm -f /root/Solace/.termux-prebuilt
INNER
    then
        echo "Update failed. You may have local changes in $UBUNTU_SOLACE_DIR." >&2
        return 1
    fi

    local version
    version="$(ubuntu_run git -C "$UBUNTU_SOLACE_DIR" rev-parse --short HEAD 2>/dev/null)"
    save_settings_value "version" "$version"
    save_settings_value "updatedAt" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    refresh_launcher_files || true
    echo "Updated to ${version:-unknown}. The next start will rebuild the projects."
}

switch_branch() {
    local new_branch="${1:-}"
    [ -n "$new_branch" ] || { echo "A branch name is required." >&2; return 1; }
    case "$new_branch" in
        -*|.*|*..*|*[!A-Za-z0-9._/-]*|*/|*//*)
            echo "Invalid branch name." >&2
            return 1
            ;;
    esac
    load_env
    [ "$new_branch" = "$INSTALL_BRANCH" ] && { echo "Already on $new_branch."; return 0; }

    set_env_value "SOLACE_BRANCH" "$new_branch"
    save_settings_value "branch" "$new_branch"
    INSTALL_BRANCH="$new_branch"
    update_solace
}

processes() {
    ps -eo pid,ppid,etime,cmd --no-headers 2>/dev/null \
        | grep -E 'aspire|Solace|dotnet|java' \
        | grep -v 'grep -E' \
        || echo "No Solace processes are running."
}

change_public_ip() {
    local new_ip="${1:-}"
    [ -n "$new_ip" ] || { echo "A public IP or hostname is required." >&2; return 1; }
    load_env

    set_env_value "SOLACE_PUBLIC_IP" "$new_ip"
    for name in WebPortal Locator AuthServer ApiServer Cdn; do
        case "$name" in
            WebPortal) port=5000 ;;
            Locator) port=8080 ;;
            AuthServer) port=8088 ;;
            ApiServer) port=8089 ;;
            Cdn) port=8090 ;;
        esac
        set_env_value "Shared__PublicEndpoints__$name" "http://$new_ip:$port"
    done
    set_env_value "BuildplateLauncher__PublicEndpoint" "$new_ip"
    echo "Public endpoint updated to $new_ip. Restart the server to apply it."
}

accept_eula() {
    load_env
    set_env_value "Shared__AcceptMinecraftEula" "true"
    echo "Minecraft EULA accepted."
}

delete_eula() {
    load_env
    set_env_value "Shared__AcceptMinecraftEula" "false"
    echo "Minecraft EULA acceptance removed."
}

reset_databases() {
    [ "${1:-}" = "--yes" ] || { echo "Refusing to reset databases without --yes." >&2; return 1; }
    load_env
    stop_server

    with_postgres 'for db in EarthDb PlayfabDb WebPortalDb; do
        PGPASSWORD="$SOLACE_DB_PASSWORD" dropdb -h 127.0.0.1 -U solace --force --if-exists "$db" 2>/dev/null || true
        PGPASSWORD="$SOLACE_DB_PASSWORD" createdb -h 127.0.0.1 -U solace -O solace "$db" 2>/dev/null || true
    done'
    rm -rf "$UBUNTU_HOME/.aspnet/DataProtection-Keys"
    echo "Databases reset."
}

uninstall_solace() {
    [ "${1:-}" = "--yes" ] || { echo "Refusing to uninstall without --yes." >&2; return 1; }
    echo "Removing Solace..."
    stop_server

    if [ -f "$TERMUX_PGDATA/.solace-managed" ]; then
        stop_postgres
        rm -rf "$TERMUX_PGDATA"
    elif [ -f "$ENV_FILE" ]; then
        set -a
        . "$ENV_FILE"
        set +a
        with_postgres 'for db in EarthDb PlayfabDb WebPortalDb; do
            PGPASSWORD="$SOLACE_DB_PASSWORD" dropdb -h 127.0.0.1 -U solace --force --if-exists "$db" 2>/dev/null || true
        done
        PGPASSWORD="$SOLACE_DB_PASSWORD" psql -h 127.0.0.1 -U solace -d postgres -c "DROP ROLE IF EXISTS solace" 2>/dev/null || true'
    fi

    rm -rf "$SOLACE_DIR" "$CONFIG_DIR" "$UBUNTU_HOME/.aspnet/DataProtection-Keys"
    rm -rf "$TERMUX_PREFIX/libexec/solace" "$TERMUX_PREFIX/share/solace"
    rm -rf "$HOME/.config/solace-launcher"
    rm -f "$TERMUX_PREFIX/bin/solace" "$TERMUX_PREFIX/bin/earth"
    echo "Solace removed. The Termux postgresql package was left installed."
}

case "${1:-}" in
    start)
        load_env
        if [ "${Shared__AcceptMinecraftEula:-false}" != "true" ]; then
            echo "The Minecraft EULA has not been accepted." >&2
            exit 42
        fi
        if is_process_alive; then
            start_dashboard_forwarder
            echo "Solace is already running."
            exit 0
        fi
        start_server || exit 1
        echo "Solace is starting."
        ;;
    stop)
        load_env
        stop_server
        echo "Solace stopped."
        ;;
    restart)
        load_env
        if [ "${Shared__AcceptMinecraftEula:-false}" != "true" ]; then
            echo "The Minecraft EULA has not been accepted." >&2
            exit 42
        fi
        stop_server
        start_server || exit 1
        echo "Solace is restarting."
        ;;
    status)
        load_env
        if [ "${2:-}" = "--json" ]; then
            status_json
            exit 0
        fi
        if is_running; then
            echo "RUNNING  $ADMIN_URL"
            dashboard="$(get_public_dashboard_url)"
            [ -n "$dashboard" ] || dashboard="$(get_dashboard_url)"
            [ -n "$dashboard" ] && echo "Dashboard: $dashboard"
            exit 0
        fi
        if is_process_alive; then
            echo "STARTING $ADMIN_URL"
            dashboard="$(get_public_dashboard_url)"
            [ -n "$dashboard" ] || dashboard="$(get_dashboard_url)"
            [ -n "$dashboard" ] && echo "Dashboard: $dashboard"
            exit 0
        fi
        echo "STOPPED"
        exit 1
        ;;
    logs)
        load_env
        service="${2:-}"
        lines="${3:-200}"
        case "$lines" in
            ''|*[!0-9]*) lines=200 ;;
        esac
        if [ -n "$service" ] && [ "$service" != "startup" ]; then
            ubuntu_dotnet timeout 60 aspire logs "$service" --apphost "$UBUNTU_APP_HOST" --tail "$lines"
            exit 0
        fi
        log_file="$STARTUP_LOG"
        cli_log="$(get_cli_log)"
        if [ -n "$cli_log" ] && [ -f "$(host_path "$cli_log")" ]; then
            log_file="$(host_path "$cli_log")"
        fi
        tail -n "$lines" "$log_file" 2>/dev/null || echo "No logs yet."
        ;;
    update)
        load_env
        update_solace
        ;;
    eula)
        case "${2:-accept}" in
            delete|--delete|revoke) delete_eula ;;
            *) accept_eula ;;
        esac
        ;;
    admin-password)
        load_env
        password="$(get_admin_password)" || {
            echo "The admin password is not available yet. Start the server once and try again." >&2
            exit 1
        }
        printf '%s\n' "$password"
        ;;
    set-public-ip)
        change_public_ip "${2:-}"
        ;;
    switch-branch)
        switch_branch "${2:-}"
        ;;
    processes)
        processes
        ;;
    reset-databases)
        reset_databases "${2:-}"
        ;;
    uninstall)
        uninstall_solace "${2:-}"
        ;;
    __dashboard-forward)
        load_env
        dashboard_forward_loop
        ;;
    health)
        echo "ok"
        ;;
    help|--help|-h)
        echo "Solace backend. Commands: start, stop, restart, status [--json], logs [service] [lines], update,"
        echo "eula [accept|delete], admin-password, set-public-ip <ip>, switch-branch <branch>, processes,"
        echo "reset-databases --yes, uninstall --yes."
        ;;
    *)
        echo "Unknown backend command: ${1:-}" >&2
        exit 2
        ;;
esac
