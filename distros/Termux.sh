#!/usr/bin/env bash
# earth — Solace for Termux (aspire)
#
# Single, self-contained command script. It runs in two contexts:
#   host (Termux)  : proxies commands into proot-distro Ubuntu; owns native Postgres.
#   distro (proot) : does the real work (install, setup, start, update).
#
# Usage:  earth [command]
#   install                Install Solace into proot-distro Ubuntu (first run)
#   setup                  Interactive configuration (.env, OIDC certs, nginx)
#   start                  Start the server in the foreground (live logs; Ctrl+C stops it)
#   status                 Show server status
#   logs [service]         Follow a service log (default: web-portal)
#   update                 Update Solace to the latest release build
#   eula [--delete]        Accept the Minecraft EULA (or delete the eula file)
#   uninstall              Completely remove Solace and its data
#   help                   Show this help

set -euo pipefail

GITHUB_REPO="${GITHUB_REPO:-cosmetide/Solace}"
INSTALL_BRANCH="aspire"
RELEASE_TAG="termux-aspire"
ARCH="linux-arm64"
ASSET="solace-termux-linux-arm64.tar.gz"
RAW="https://raw.githubusercontent.com/${GITHUB_REPO}/${INSTALL_BRANCH}"

SELF_PATH="$(realpath -m "$0" 2>/dev/null || printf '%s' "$0")"
DISTRO_NAME="ubuntu"
PG_DATA_HOST="${HOME}/.solace/pgdata"
PG_LOG="${HOME}/.solace/state/postgres.log"
if [ -z "${EARTH_INSIDE:-}" ]; then
    DISTRO_ROOTFS="${PROOT_DISTRO_ROOT:-${PREFIX:-/data/data/com.termux/files/usr}/var/lib/proot-distro}/containers/${DISTRO_NAME}"
    LEGACY_ROOTFS="${PROOT_DISTRO_ROOT:-${PREFIX:-/data/data/com.termux/files/usr}/var/lib/proot-distro}/installed-rootfs/${DISTRO_NAME}"
    DISTRO_MIRROR="${PREFIX:-/data/data/com.termux/files/usr}/tmp/.earth-self.sh"
fi

c_red='\033[1;31m'; c_grn='\033[1;32m'; c_ylw='\033[1;33m'; c_cyn='\033[1;36m'; c_rst='\033[0m'
err()  { echo -e "${c_red}[ERROR] $1${c_rst}" >&2; }
warn() { echo -e "${c_ylw}[WARN] $1${c_rst}" >&2; }
ok()   { echo -e "${c_grn}[OK] $1${c_rst}"; }
info() { echo -e "${c_cyn}> $1${c_rst}"; }
step() { echo ""; echo -e "${c_cyn}== $1 ==${c_rst}"; }

# ─── DISTRO (in-proot) context ────────────────────────────────────────────

SOLACE_DIR="${HOME}/Solace"
BIN="${SOLACE_DIR}/bin"
RUN="${SOLACE_DIR}/run"
LOGS="${SOLACE_DIR}/logs"
STATICDATA="${SOLACE_DIR}/staticdata"
ENV_FILE="${SOLACE_DIR}/.env"
DOTNET_ROOT="${HOME}/.dotnet"
JAVA_HOME="${SOLACE_DIR}/java/jre"
SERVICES="object-store event-bus buildplate-server-setup buildplate-updater buildplate-launcher api-server cdn auth-server web-portal locator tappable-generator tile-renderer"
SERVICES_REVERSE="tile-renderer tappable-generator locator web-portal auth-server cdn api-server buildplate-launcher buildplate-updater buildplate-server-setup event-bus object-store"
SVC_PORTS="web-portal:5000 api-server:8089 cdn:8090 auth-server:8088 locator:8080 object-store:18080 event-bus:18081 buildplate-updater:18083 buildplate-launcher:18084 tappable-generator:18085 tile-renderer:18086"

declare -A ENV
load_env() {
    [ -f "$ENV_FILE" ] || { err "No '.env' found — run 'earth setup' first."; exit 1; }
    while IFS='=' read -r k v; do
        k="${k%%[[:space:]]*}"
        v="${v%$'\r'}"
        [ -z "$k" ] && continue
        case "$k" in \#*) continue ;; esac
        v="${v#\"}"; v="${v%\"}"
        ENV["$k"]="$v"
    done < "$ENV_FILE"
}
get() { echo "${ENV[${1:-}]:-${2:-}}"; }
norm_bool() {
    case "${1:-}" in
        1|true|TRUE|yes|YES|on) echo "true" ;;
        *) echo "false" ;;
    esac
}

conn() { printf 'Host=127.0.0.1;Port=5432;Username=postgres;Password=%s;Database=%s' "$(get POSTGRES_PASSWORD)" "$1"; }

pid_of()    { [ -f "$RUN/$1.pid" ] && cat "$RUN/$1.pid" || true; }
svc_alive() {
    local p z
    p="$(pid_of "$1")"
    [ -n "$p" ] || return 1
    kill -0 "$p" 2>/dev/null || return 1
    z="$(ps -o stat= -p "$p" 2>/dev/null | tr -d ' ')"
    case "$z" in
        Z*|'') return 1 ;;   # zombies count as down
    esac
    return 0
}

wait_health() {
    local name="$1" port="$2" tries="${3:-90}" i=0
    while [ "$i" -lt "$tries" ]; do
        if ( exec 3<>"/dev/tcp/127.0.0.1/$port" ) 2>/dev/null; then
            ok "$name is up on :$port (~${i}s)"
            return 0
        fi
        sleep 1; i=$((i+1))
    done
    warn "$name did not open :$port — check $LOGS/$name.log"
    return 1
}

stop_svc() {
    local name="$1" p z i=0
    p="$(pid_of "$name")"
    [ -n "$p" ] || return 0
    z="$(ps -o stat= -p "$p" 2>/dev/null | tr -d ' ')"
    case "$z" in
        Z*|'') rm -f "$RUN/$name.pid"; return 0 ;;
    esac
    kill "$p" 2>/dev/null || true
    while [ "$i" -lt 15 ] && kill -0 "$p" 2>/dev/null; do sleep 1; i=$((i+1)); done
    kill -9 "$p" 2>/dev/null || true
    rm -f "$RUN/$name.pid"
    ok "$name stopped"
}
teardown_services() {
    for s in $SERVICES_REVERSE; do stop_svc "$s"; done
    if pgrep -x nginx >/dev/null 2>&1; then
        pkill -x nginx 2>/dev/null || true
        rm -f "$RUN/nginx.pid"
        ok "nginx stopped"
    fi
}

dotnet_app() {
    local name="$1" app="$2" port="$3"; shift 3
    if svc_alive "$name"; then info "$name already running"; return 0; fi
    [ -x "$DOTNET_ROOT/dotnet" ] || { err ".NET runtime missing — run 'earth install'."; exit 1; }
    [ -f "$BIN/$name/$app" ] || { err "Missing $BIN/$name/$app — run 'earth update'."; exit 1; }
    : > "$LOGS/$name.log"
    info "Starting $name on :$port"
    setsid nohup env "$@" "$DOTNET_ROOT/dotnet" "$BIN/$name/$app" \
        >> "$LOGS/$name.log" 2>&1 < /dev/null &
    echo $! > "$RUN/$name.pid"
    wait_health "$name" "$port" || true
}

dotnet_job() {
    local name="$1" app="$2"; shift 2
    [ -x "$DOTNET_ROOT/dotnet" ] || { err ".NET runtime missing — run 'earth install'."; exit 1; }
    [ -f "$BIN/$name/$app" ] || { err "Missing $BIN/$name/$app — run 'earth update'."; exit 1; }
    info "Running $name (one-shot job)..."
    local rc=0
    : > "$LOGS/$name.log"
    env "$@" "$DOTNET_ROOT/dotnet" "$BIN/$name/$app" >> "$LOGS/$name.log" 2>&1 || rc=$?
    if [ "$rc" -eq 0 ]; then
        ok "$name completed"
        : > "$RUN/$name.done"
    else
        warn "$name exited with code $rc — see $LOGS/$name.log"
    fi
}

pg_sql()      { psql -h 127.0.0.1 -p 5432 -U postgres "$@"; }
pg_createdb() { createdb -h 127.0.0.1 -p 5432 -U postgres "$1"; }

ensure_postgres() {
    if pg_isready -h 127.0.0.1 -p 5432 -q 2>/dev/null; then
        ok "Host Postgres already running on :5432"
    else
        info "Waiting for the Termux-host Postgres on :5432 (started by 'earth start')..."
        local i=0
        while ! pg_isready -h 127.0.0.1 -p 5432 -q 2>/dev/null; do
            [ "$i" -ge 30 ] && { err "Host Postgres did not come up — check ${PG_LOG}"; exit 1; }
            sleep 1; i=$((i+1))
        done
    fi
}

pg_conf() {
    local pgpass; pgpass="$(get POSTGRES_PASSWORD)"
    if [ -n "$pgpass" ]; then
        pg_sql -c "ALTER USER postgres WITH PASSWORD '$pgpass';" >/dev/null 2>&1 \
            || warn "Could not set postgres password"
    fi
    for db in EarthDb PlayfabDb WebPortalDb; do
        pg_sql -tAc "SELECT 1 FROM pg_database WHERE datname='$db'" | grep -q 1 \
            || pg_createdb "$db" || warn "Could not create db $db"
    done
    ok "Postgres ready (dbs: EarthDb PlayfabDb WebPortalDb)"
}

run_services() {
    local eula static
    eula="$(norm_bool "$(get SHARED_ACCEPTMINECRAFTEULA false)")"
    static="$STATICDATA"

    [ -d "$STATICDATA" ] || warn "staticdata missing at $STATICDATA — run 'earth install' first"

    dotnet_app object-store ObjectStoreServer.dll 18080 \
        "ASPNETCORE_URLS=http://127.0.0.1:18080" \
        "Kestrel__EndpointDefaults__Protocols=Http2" \
        "DataDirectory=${SOLACE_DIR}/data/object_store"

    dotnet_app event-bus EventBusServer.dll 18081 \
        "ASPNETCORE_URLS=http://127.0.0.1:18081" \
        "Kestrel__EndpointDefaults__Protocols=Http2"

    dotnet_job buildplate-server-setup ServerSetup.dll \
        "AcceptMinecraftEula=$eula" \
        "StaticDataPath=$static"

    dotnet_app buildplate-updater BuildplateUpdater.dll 18083 \
        "ASPNETCORE_URLS=http://127.0.0.1:18083" \
        "AcceptMinecraftEula=$eula" \
        "StaticDataPath=$static" \
        "services__event-bus__http__0=http://127.0.0.1:18081" \
        "services__buildplate-server-setup__http__0=http://127.0.0.1:18082"

    dotnet_app buildplate-launcher BuildplateLauncher.dll 18084 \
        "ASPNETCORE_URLS=http://127.0.0.1:18084" \
        "PublicEndpoint=$(get BUILDPLATELAUNCHER_PUBLICENDPOINT)" \
        "BaseInstancePublicPort=$(get BUILDPLATELAUNCHER_BASEINSTANCEPUBLICPORT 19132)" \
        "FountainBridgeJarName=fountain-{{version}}-SNAPSHOT-jar-with-dependencies.jar" \
        "FabricJarName=server-{{version}}.jar" \
        "ConnectorPluginJarName=buildplate-connector-plugin-{{version}}-SNAPSHOT-jar-with-dependencies.jar" \
        "AcceptMinecraftEula=$eula" \
        "StaticDataPath=$static" \
        "services__event-bus__http__0=http://127.0.0.1:18081" \
        "services__buildplate-server-setup__http__0=http://127.0.0.1:18082"

    dotnet_app api-server ApiServer.dll 8089 \
        "ASPNETCORE_URLS=http://127.0.0.1:8089" \
        "Authentication__LocalLoginOnly=$(get APISERVER_AUTHENTICATION_LOCALLOGINONLY false)" \
        "FixUpBuildplatesOnImport=$(get SHARED_FIXUPBUILDPLATESONIMPORT false)" \
        "StaticDataPath=$static" \
        "ConnectionStrings__EarthDb=$(conn EarthDb)" \
        "ConnectionStrings__PlayfabDb=$(conn PlayfabDb)" \
        "services__event-bus__http__0=http://127.0.0.1:18081" \
        "services__object-store__http__0=http://127.0.0.1:18080"

    dotnet_app cdn Cdn.dll 8090 \
        "ASPNETCORE_URLS=http://127.0.0.1:8090" \
        "StaticDataPath=$static" \
        "ConnectionStrings__EarthDb=$(conn EarthDb)" \
        "services__event-bus__http__0=http://127.0.0.1:18081" \
        "services__object-store__http__0=http://127.0.0.1:18080"

    dotnet_app auth-server Solace.AuthServer.dll 8088 \
        "ASPNETCORE_URLS=http://127.0.0.1:8088" \
        "Oidc__ClientId=solace-auth-server" \
        "Oidc__ClientSecret=$(get SHARED_OIDC_WEBPORTAL_AUTHSERVER_CLIENTSECRET)" \
        "Oidc__DisplayName=Solace" \
        "Oidc__AllowInsecure=$(get SHARED_OIDC_WEBPORTAL_AUTHSERVER_ALLOWINSECURE)" \
        "PublicEndpoints__WebPortal=$(get SHARED_PUBLICENDPOINTS_WEBPORTAL)" \
        "Captcha__Provider=$(get SHARED_CAPTCHA_PROVIDER NoOp)" \
        "Captcha__CloudflareTurnstileSiteKey=$(get SHARED_CAPTCHA_CLOUDFLARETURNSTILESITEKEY)" \
        "Captcha__CloudflareTurnstileSecretKey=$(get SHARED_CAPTCHA_CLOUDFLARETURNSTILESECRETKEY)" \
        "StaticDataPath=$static" \
        "ConnectionStrings__EarthDb=$(conn EarthDb)" \
        "ConnectionStrings__PlayfabDb=$(conn PlayfabDb)" \
        "services__event-bus__http__0=http://127.0.0.1:18081" \
        "services__web-portal__http__0=http://127.0.0.1:5000"

    dotnet_app web-portal Solace.WebPortal.dll 5000 \
        "ASPNETCORE_URLS=http://127.0.0.1:5000" \
        "Oidc__AuthServer__ClientId=solace-auth-server" \
        "Oidc__AuthServer__ClientSecret=$(get SHARED_OIDC_WEBPORTAL_AUTHSERVER_CLIENTSECRET)" \
        "Oidc__AuthServer__DisplayName=Solace" \
        "Oidc__AuthServer__AllowInsecure=$(norm_bool "$(get SHARED_OIDC_WEBPORTAL_AUTHSERVER_ALLOWINSECURE)")" \
        "Oidc__SigningCertPassword=$(get SHARED_OIDC_WEBPORTAL_SIGNINGCERTPASSWORD)" \
        "Oidc__EncryptionCertPassword=$(get SHARED_OIDC_WEBPORTAL_ENCRYPTIONCERTPASSWORD)" \
        "Oidc__SigningCertPath=${SOLACE_DIR}/certs/web-portal/oidc-signing-cert.pfx" \
        "Oidc__EncryptionCertPath=${SOLACE_DIR}/certs/web-portal/oidc-encryption-cert.pfx" \
        "PublicEndpoints__WebPortal=$(get SHARED_PUBLICENDPOINTS_WEBPORTAL)" \
        "PublicEndpoints__Locator=$(get SHARED_PUBLICENDPOINTS_LOCATOR)" \
        "PublicEndpoints__AuthServer=$(get SHARED_PUBLICENDPOINTS_AUTHSERVER)" \
        "Captcha__Provider=$(get SHARED_CAPTCHA_PROVIDER NoOp)" \
        "Captcha__CloudflareTurnstileSiteKey=$(get SHARED_CAPTCHA_CLOUDFLARETURNSTILESITEKEY)" \
        "Captcha__CloudflareTurnstileSecretKey=$(get SHARED_CAPTCHA_CLOUDFLARETURNSTILESECRETKEY)" \
        "AdminAccountPassword=$(get WEBPORTAL_ADMINACCOUNTPASSWORD)" \
        "BuildplatePreview__Enabled=$(get WEBPORTAL_BUILDPLATEPREVIEW_ENABLED true)" \
        "BuildplatePreview__GenerationMaxConcurrency=$(get WEBPORTAL_BUILDPLATEPREVIEW_GENERATIONMAXCONCURRENCY 1)" \
        "FixUpBuildplatesOnImport=$(get SHARED_FIXUPBUILDPLATESONIMPORT false)" \
        "PORT_SELF=5000" \
        "StaticDataPath=$static" \
        "ConnectionStrings__EarthDb=$(conn EarthDb)" \
        "ConnectionStrings__PlayfabDb=$(conn PlayfabDb)" \
        "ConnectionStrings__WebPortalDb=$(conn WebPortalDb)" \
        "services__event-bus__http__0=http://127.0.0.1:18081" \
        "services__object-store__http__0=http://127.0.0.1:18080"

    dotnet_app locator Locator.dll 8080 \
        "ASPNETCORE_URLS=http://127.0.0.1:8080" \
        "PublicEndpoints__ApiServer=$(get SHARED_PUBLICENDPOINTS_APISERVER)" \
        "PublicEndpoints__Cdn=$(get SHARED_PUBLICENDPOINTS_CDN)" \
        "services__api-server__http__0=http://127.0.0.1:8089" \
        "services__cdn__http__0=http://127.0.0.1:8090"

    dotnet_app tappable-generator TappablesGenerator.dll 18085 \
        "ASPNETCORE_URLS=http://127.0.0.1:18085" \
        "StaticDataPath=$static" \
        "services__event-bus__http__0=http://127.0.0.1:18081"

    dotnet_app tile-renderer TileRenderer.dll 18086 \
        "ASPNETCORE_URLS=http://127.0.0.1:18086" \
        "TileSource__TileJsonUrl=$(get TILERENDERER_TILESOURCE_TILEJSONURL)" \
        "TileSource__TileDatabaseConnectionString=$(get TILERENDERER_TILESOURCE_TILEDATABASECONNECTIONSTRING)" \
        "StaticDataPath=$static" \
        "services__event-bus__http__0=http://127.0.0.1:18081"
}

start_nginx() {
    [ -f "$SOLACE_DIR/nginx.conf" ] || { warn "No nginx.conf yet — web portal reachable at http://127.0.0.1:5000"; return 0; }
    pgrep -x nginx >/dev/null 2>&1 && { ok "nginx already running"; return 0; }
    info "Starting nginx..."
    nginx -c "$SOLACE_DIR/nginx.conf" -g 'daemon off;' >> "$LOGS/nginx.log" 2>&1 &
    echo $! > "$RUN/nginx.pid"
    sleep 1
    ok "nginx started"
}

# static data reconciliation (the staticdata repo drifts from what the app expects)

reconcile_staticdata() {
    local ns="Earth-Restored Solace.StaticData" renamed=0 dropped=0 dir zsz name new_uuid info z pf
    pf="${STATICDATA}/playfab"

    step "3a. BUILDPLATE GUID NORMALIZATION"
    while IFS= read -r dir; do
        for zsz in "${dir}"/*.zip; do
            [ -f "${zsz}" ] || continue
            name="$(basename "${zsz}" .zip)"
            if ! printf '%s' "$name" | grep -qE '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'; then
                local h v
                h="$(printf '%s:%s' "${ns}" "${name}" | md5sum | awk '{print $1}')"
                v="$(printf '%x' "$(( (16#${h:16:1} & 3) | 8 ))")"
                new_uuid="$(printf '%s-%s-%s-%s%s-%s' "${h:0:8}" "${h:8:4}" "3${h:13:3}" "${v}" "${h:17:3}" "${h:20:12}")"
                mv "${zsz}" "$(dirname "${zsz}")/${new_uuid}.zip"
                [ -f "$(dirname "${zsz}")/${name}.info" ] && mv "$(dirname "${zsz}")/${name}.info" "$(dirname "${zsz}")/${new_uuid}.info"
                renamed=$((renamed + 1))
            fi
        done
    done < <(find "${STATICDATA}/buildplates" -type d 2>/dev/null || true)
    [ "$renamed" -gt 0 ] && echo "      renamed ${renamed} buildplate file(s) to GUID names"

    step "3b. BUILDPLATE METADATA SIDECARS"
    while IFS= read -r dir; do
        for z in "${dir}"/*.zip; do
            [ -f "${z}" ] || continue
            info="$(dirname "${z}")/$(basename "${z}" .zip).info"
            [ -f "$info" ] && [ -s "$info" ] && continue
            if unzip -p "${z}" buildplate_metadata.json > "$info" 2>/dev/null && [ -s "$info" ]; then
                echo "    . $(basename "$info")"
            else
                rm -f "$info"
            fi
        done
    done < <(find "${STATICDATA}/buildplates" -type d 2>/dev/null || true)

    step "3c. PLAYFAB DATA RECONCILIATION"
    if [ -d "${pf}" ]; then
        [ ! -f "${pf}/version.txt" ] && printf '0.0.1\n' > "${pf}/version.txt"
        [ -d "${pf}/shop_tabs" ] && [ ! -d "${pf}/store_tabs" ] && ln -s shop_tabs "${pf}/store_tabs"
        [ -f "${pf}/shop_not_search_query_tags.txt" ] && [ ! -f "${pf}/store_not_search_query_tags.txt" ] \
            && ln -s shop_not_search_query_tags.txt "${pf}/store_not_search_query_tags.txt"
    fi

    step "3d. PLAYFAB ITEM TYPE NORMALIZATION"
    if [ -d "${pf}/items" ]; then
        for item in "${pf}/items"/*.json; do
            [ -f "${item}" ] || continue
            if grep -q '"Type": "Buildplate"\|"Type": "InventoryItem"' "${item}"; then continue; fi
            rm -f "${item}"; dropped=$((dropped + 1))
        done
        [ "$dropped" -gt 0 ] && echo "      dropped ${dropped} item(s) with unsupported data type"
    fi
    return 0
}

install_dotnet() {
    dotnet_real="${DOTNET_ROOT}/dotnet"
    if [ -x "$dotnet_real" ] && "$dotnet_real" --list-runtimes 2>/dev/null | grep -q "Microsoft.AspNetCore.App 11.0"; then
        ok ".NET 11 runtime present"
        return 0
    fi
    info "Installing .NET 11 aspnetcore runtime (clearing any stale install)..."
    rm -rf "$DOTNET_ROOT"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --channel 11.0 --quality preview --runtime aspnetcore --install-dir "$DOTNET_ROOT"
    rm -f /tmp/dotnet-install.sh
    ok ".NET 11 runtime installed"
}

_update_release() {
    (
        set -euo pipefail
        local TMP COMMIT EXPECTED_SHA ACTUAL_SHA STAGE
        TMP="$(mktemp -d "${TMPDIR:-/tmp}/solace_dl_XXXXXX")"
        trap 'rm -rf "$TMP"' EXIT
        mkdir -p "$SOLACE_DIR" "$BIN" "$SOLACE_DIR/java"

        info "Fetching release metadata ($GITHUB_REPO @ $RELEASE_TAG)..."
        [ -z "${GITHUB_TOKEN:-}" ] && GITHUB_TOKEN=""
        local auth=()
        [ -n "$GITHUB_TOKEN" ] && auth=(-H "Authorization: Bearer $GITHUB_TOKEN")
        curl -fsSL "${auth[@]:-}" "https://api.github.com/repos/${GITHUB_REPO}/releases/tags/${RELEASE_TAG}" \
            > "${TMP}/release.json" || { err "Release ${RELEASE_TAG} not found (workflow may not have run yet)"; exit 1; }

        COMMIT="$(jq -r '.target_commitish // .tag_name // ""' "${TMP}/release.json" 2>/dev/null)"
        info "Release commit: ${COMMIT}"

        curl -fsSL -o "${TMP}/manifest.json" \
            "https://github.com/${GITHUB_REPO}/releases/download/${RELEASE_TAG}/manifest.json"
        EXPECTED_SHA="$(jq -r --arg name "$ASSET" '.assets[] | select(.name == $name) | .sha256' "${TMP}/manifest.json")"
        [ -n "$EXPECTED_SHA" ] || { err "No checksum for $ASSET in manifest"; exit 1; }

        info "Downloading ${ASSET} (may take a while)..."
        curl -fsSL -o "${TMP}/${ASSET}" \
            "https://github.com/${GITHUB_REPO}/releases/download/${RELEASE_TAG}/${ASSET}" \
            || { err "Failed to download ${ASSET}"; exit 1; }

        ACTUAL_SHA="$(sha256sum "${TMP}/${ASSET}" | cut -d' ' -f1)"
        [ "$ACTUAL_SHA" = "$EXPECTED_SHA" ] || { err "Checksum mismatch for ${ASSET}"; exit 1; }

        info "Extracting..."
        STAGE="${TMP}/bundle"
        mkdir -p "${STAGE}"
        tar -xzf "${TMP}/${ASSET}" -C "$STAGE"

        rm -rf "$BIN"
        mv "$STAGE/bin" "$BIN"
        rm -rf "$SOLACE_DIR/java"
        mv "$STAGE/java" "$SOLACE_DIR/java"
        chmod -R +x "$BIN" 2>/dev/null || true
        chmod -R +x "$SOLACE_DIR/java/jre" 2>/dev/null || true

        printf '%s\n' "$COMMIT" > "$SOLACE_DIR/version.txt"
        touch "$BIN/.downloaded"
        ok "Solace installed (commit ${COMMIT})"
    )
}

_distro_install() {
    step "1. INSTALLING PACKAGES"
    apt-get update -y >/dev/null
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        ca-certificates curl wget git openssl unzip jq tar nginx postgresql-client
    ok "packages installed"

    step "2. DIRECTORIES"
    mkdir -p "$SOLACE_DIR"/{bin,logs,run,data/object_store,java,certs/web-portal}
    mkdir -p "$STATICDATA"
    ok "directories ready at $SOLACE_DIR"

    step "3. STATIC DATA"
    if [ ! -f "$STATICDATA/.git" ] && [ ! -d "$STATICDATA/indices" ]; then
        if [ -d "$STATICDATA" ] && [ -n "$(ls -A "$STATICDATA" 2>/dev/null)" ]; then
            echo "    ... stale staticdata detected — replacing it"
            rm -rf "$STATICDATA"
        fi
        git clone --depth 1 https://github.com/Earth-Restored/Solace.StaticData "$STATICDATA" \
            || { err "Could not clone Solace.StaticData (network?)"; exit 1; }
    else
        ok "staticdata present"
    fi
    reconcile_staticdata

    step "4. .NET 11 RUNTIME"
    install_dotnet
    export DOTNET_ROOT DOTNET_CLI_TELEMETRY_OPTOUT=1
    export PATH="$DOTNET_ROOT:$PATH"

    step "5. BINARIES"
    [ -f "$BIN/.downloaded" ] && echo "Binaries already present ('earth update' for latest)." \
        || _update_release || err "Binary download failed — try 'earth update' later."

    step "6. COMPLETE"
    echo ""
    echo "System ready. Everything lives in: $SOLACE_DIR"
    echo "Next:"
    echo "  1. earth setup   — configure endpoint, .env, OIDC certs"
    echo "  2. earth start   — boot the server (Ctrl+C stops it)"
    echo "  3. Admin panel:  http://127.0.0.1:5000"
    ok "bootstrap done"
}

ask() {
    local prompt="$1" default="${2:-}" answer
    if [ -n "$default" ]; then
        read -rp "$prompt [$default]: " answer || true
        printf '%s' "${answer:-$default}"
    else
        read -rp "$prompt: " answer || true
        printf '%s' "$answer"
    fi
}
ask_yes() {
    local a; a="$(ask "$1 (y/N)" "${2:-n}")"
    case "$a" in y|Y|yes|YES) return 0 ;; esac
    return 1
}
rand_hex() { openssl rand -hex "${1:-32}"; }

_distro_setup() {
    command -v openssl >/dev/null 2>&1 \
        || DEBIAN_FRONTEND=noninteractive apt-get install -y openssl >/dev/null

    if [ -f "$ENV_FILE" ]; then
        echo ""
        warn "'$ENV_FILE' already exists."
        if ask_yes "Keep it (skip regenerating .env/certs/nginx)?" "n"; then
            echo ""
            ok "Using existing configuration."
            exit 0
        fi
        cp "$ENV_FILE" "${ENV_FILE}.bak" 2>/dev/null || true
    fi

    echo ""
    info "Solace native setup"
    echo "  Everything runs inside proot Ubuntu; no Docker, no systemd."
    echo "  To connect from other devices, use your phone's LAN IP."

    HOST="$(ask "Server host/IP on your LAN" "127.0.0.1")"; HOST="${HOST:-127.0.0.1}"
    [ "$HOST" = "localhost" ] && HOST="127.0.0.1"

    allowInsecure=1
    if ! printf '%s' "$HOST" | grep -qE '^[0-9.]+$'; then
        if ! ask_yes "Host is a domain (TLS assumed). Allow insecure OIDC anyway?" "n"; then
            allowInsecure=0
        fi
    fi

    eula=0
    if ask_yes "Accept the Minecraft EULA (required for buildplates)?" "n"; then eula=1; fi

    sign_pass="$(ask "OIDC signing cert password (blank = random)" "")"
    [ -z "$sign_pass" ] && sign_pass="$(rand_hex 16)"
    enc_pass="$(ask "OIDC encryption cert password (blank = random)" "")"
    [ -z "$enc_pass" ] && enc_pass="$(rand_hex 16)"

    CERTS_DIR="$SOLACE_DIR/certs/web-portal"
    mkdir -p "$CERTS_DIR"
    info "Generating OIDC PFX certificates with openssl..."
    gen_oidc_cert() {
        local name="$1" pass="$2" tmp; tmp="$(mktemp -d)"
        openssl req -x509 -newkey rsa:2048 -keyout "${tmp}/key.pem" -out "${tmp}/cert.pem" \
            -days 36500 -nodes -subj "/CN=Solace OIDC ${name} Cert" >/dev/null 2>&1
        openssl pkcs12 -export -out "${CERTS_DIR}/oidc-${name}-cert.pfx" \
            -inkey "${tmp}/key.pem" -in "${tmp}/cert.pem" -passout "pass:${pass}" >/dev/null 2>&1
        rm -rf "$tmp"
    }
    gen_oidc_cert signing "$sign_pass"
    gen_oidc_cert encryption "$enc_pass"
    ok "certs created in $CERTS_DIR"

    # Wait: the .env values must match the HOST/allowInsecure/eula answers above.
    info "Writing ${ENV_FILE}..."
    cat > "$ENV_FILE" <<EOF
# Solace native (proot Ubuntu) environment — generated by 'earth setup'.

POSTGRES_PASSWORD=$(rand_hex 24)

SHARED_ACCEPTMINECRAFTEULA=$( [ "${eula}" = "1" ] && echo true || echo false )

SHARED_PUBLICENDPOINTS_WEBPORTAL=http://${HOST}:5000
SHARED_PUBLICENDPOINTS_LOCATOR=http://${HOST}:8080
SHARED_PUBLICENDPOINTS_AUTHSERVER=http://${HOST}:8088
SHARED_PUBLICENDPOINTS_APISERVER=http://${HOST}:8089
SHARED_PUBLICENDPOINTS_CDN=http://${HOST}:8090
BUILDPLATELAUNCHER_PUBLICENDPOINT=${HOST}
BUILDPLATELAUNCHER_BASEINSTANCEPUBLICPORT=19132

SHARED_CAPTCHA_PROVIDER=NoOp
SHARED_CAPTCHA_CLOUDFLARETURNSTILESITEKEY=
SHARED_CAPTCHA_CLOUDFLARETURNSTILESECRETKEY=
SHARED_FIXUPBUILDPLATESONIMPORT=false
SHARED_OIDC_WEBPORTAL_AUTHSERVER_CLIENTSECRET=$(rand_hex 32)
SHARED_OIDC_WEBPORTAL_AUTHSERVER_ALLOWINSECURE=$( [ "${allowInsecure}" = "1" ] && echo true || echo false)
SHARED_OIDC_WEBPORTAL_SIGNINGCERTPASSWORD=${sign_pass}
SHARED_OIDC_WEBPORTAL_ENCRYPTIONCERTPASSWORD=${enc_pass}

WEBPORTAL_ADMINACCOUNTPASSWORD=$(rand_hex 12)
WEBPORTAL_BUILDPLATEPREVIEW_ENABLED=true
WEBPORTAL_BUILDPLATEPREVIEW_GENERATIONMAXCONCURRENCY=1

APISERVER_AUTHENTICATION_LOCALLOGINONLY=false

TILERENDERER_TILESOURCE_TILEJSONURL=https://tiles.openfreemap.org/planet
TILERENDERER_TILESOURCE_TILEDATABASECONNECTIONSTRING=
EOF
    ok ".env written"

    info "Rendering nginx.conf (port 80 -> web-portal :5000)..."
    cat > "$SOLACE_DIR/nginx.conf" <<'EOF'
events { worker_connections 1024; }

http {
    server {
        listen 80;
        server_name __PUBLIC_HOST__;

        location / {
            proxy_http_version 1.1;
            proxy_set_header Host $http_host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection $http_connection;
            proxy_pass http://127.0.0.1:5000;
        }
    }
}
EOF
    sed -i "s/__PUBLIC_HOST__/${HOST}/g" "$SOLACE_DIR/nginx.conf"
    ok "nginx.conf ready"

    mkdir -p "$SOLACE_DIR/staticdata/resourcepacks/genoa"
    echo ""
    warn "Final step (optional): resource pack support"
    echo "  To let users play on your buildplates without patching the APK, put a"
    echo "  resource pack in: $SOLACE_DIR/staticdata/resourcepacks/genoa/"
    echo "  Example source (needs Wayback Machine):"
    echo "    https://cdn.mceserv.net/availableresourcepack/resourcepacks/dba38e59-091a-4826-b76a-a08d7de5a9e2-1301b0c257a311678123b9e7325d0d6c61db3c35"
    echo ""
    echo -e "${c_grn}Configuration complete.${c_rst}"
    echo "  .env:       $ENV_FILE   (contains generated secrets — keep safe)"
    echo "  OIDC certs: $CERTS_DIR"
    echo "  nginx.conf: $SOLACE_DIR/nginx.conf"
    echo ""
    echo "Next: 'earth start'"
    if [ "$allowInsecure" = "1" ]; then
        warn "OIDC is in INSECURE (http) mode — fine for LAN/single-user, do not expose to the open internet."
    fi
}

_distro_daemon() {
    mkdir -p "$RUN" "$LOGS"
    [ -f "$BIN/.downloaded" ] || { err "No Solace binaries found — run 'earth install'."; exit 1; }
    [ -d "$BIN/web-portal" ] || { err "Binaries incomplete — run 'earth update'."; exit 1; }
    [ -f "$ENV_FILE" ] || { err "No '.env' found — run 'earth setup' first."; exit 1; }

    # Recover from any leftover processes of a crashed/previous session.
    teardown_services

    load_env
    export PATH="$DOTNET_ROOT:$PATH"
    export DOTNET_ROOT DOTNET_CLI_TELEMETRY_OPTOUT=1
    # Workstation GC — Server GC reserves 256 GiB of VM and fails on low-memory devices.
    export DOTNET_gcServer=0
    export JAVA_HOME

    ensure_postgres
    pg_conf
    run_services
    start_nginx

    echo ""
    ok "Solace is up."
    echo "  Admin panel: http://127.0.0.1:5000"
    echo "  EULA:        earth eula"
    echo ""
    echo "  Live logs below — press Ctrl+C to stop the server."
    echo "  --------------------------------------------------"

    trap 'teardown_services; ok "Solace stopped."' INT TERM HUP EXIT
    tail -q -F "$LOGS/web-portal.log" "$LOGS/api-server.log" 2>/dev/null
    return 0
}

_distro_status() {
    echo "- Solace status -"
    for s in $SERVICES; do
        if svc_alive "$s"; then
            echo -e "  ${c_grn}[RUNNING]${c_rst} $s (pid $(pid_of "$s"))"
        elif [ "$s" = "buildplate-server-setup" ] && [ -f "$RUN/$s.done" ]; then
            echo -e "  ${c_grn}[DONE]${c_rst}     $s (one-shot, completed)"
        else
            echo -e "  ${c_red}[STOPPED]${c_rst} $s"
        fi
    done
    pgrep -x nginx >/dev/null 2>&1 \
        && echo -e "  ${c_grn}[RUNNING]${c_rst} nginx" \
        || echo -e "  ${c_red}[STOPPED]${c_rst} nginx"
    pg_isready -h 127.0.0.1 -p 5432 -q 2>/dev/null \
        && echo -e "  ${c_grn}[RUNNING]${c_rst} postgres (host)" \
        || echo -e "  ${c_red}[STOPPED]${c_rst} postgres (host)"
    echo ""
    echo "Ports: $SVC_PORTS"
}

_distro_logs() {
    local svc="${1:-web-portal}"
    [ -f "$LOGS/$svc.log" ] || { err "No log for '$svc' yet — is it running?"; exit 1; }
    echo "Following: $svc — ${LOGS}/${svc}.log (Ctrl+C to stop)"
    tail -n 120 -f "$LOGS/$svc.log"
}

_distro_eula() {
    export TERM=xterm
    local eula_file="${STATICDATA}/server_template_dir/eula.txt" action="${1:-}"
    if [ "$action" = "--delete" ]; then
        rm -f "$eula_file"
        echo "[Solace]: The eula file has been deleted."
        exit 0
    fi
    if [ ! -f "$eula_file" ]; then
        echo "[Solace]: EULA file not found. Start the server once first."
        exit 1
    fi
    if grep -q "eula=true" "$eula_file"; then
        echo "[Solace]: EULA already accepted."
        exit 0
    fi
    printf "Read the EULA: https://aka.ms/MinecraftEULA\n"
    printf "Type YES to agree.\nAccept EULA > "
    IFS= read -r CONFIRM
    CONFIRM="$(printf '%s' "$CONFIRM" | tr -d '\r\n')"
    if [ "$CONFIRM" = "YES" ]; then
        grep -q "eula=false" "$eula_file" \
            && sed -i 's/eula=false/eula=true/g' "$eula_file" \
            || echo "eula=true" >> "$eula_file"
        echo "[Solace]: EULA accepted."
    else
        echo "[Solace]: EULA not accepted."
    fi
}

_distro_uninstall() {
    teardown_services
    rm -rf "$SOLACE_DIR"
    rm -f "$DISTRO_MIRROR"
    ok "Solace removed from this container."
}

_distro_main() {
    local action="${1:-}"; [ $# -gt 0 ] && shift
    case "$action" in
        _install)   _distro_install ;;
        _setup)     _distro_setup ;;
        _update)    _update_release ;;
        _daemon)    _distro_daemon ;;
        _status)    _distro_status ;;
        _logs)      _distro_logs "${1:-web-portal}" ;;
        _eula)      _distro_eula "$@" ;;
        _uninstall) _distro_uninstall ;;
        *) err "unknown distro action: $action"; exit 2 ;;
    esac
}

# ─── HOST (Termux) context ────────────────────────────────────────────────

is_termux() { [ -n "$TERMUX_VERSION" ] || printf '%s' "$PREFIX" | grep -q "com.termux"; }

ensure_proot() {
    command -v proot-distro >/dev/null 2>&1 \
        || { err "proot-distro is not installed. Run: pkg install proot-distro, then: earth install"; exit 1; }
}

ensure_distro() {
    local out
    if [ -d "$DISTRO_ROOTFS" ] || [ -d "$LEGACY_ROOTFS" ]; then return 0; fi
    if proot-distro list 2>&1 | grep -qE "(^|[[:space:]])${DISTRO_NAME}([[:space:]]|$)"; then return 0; fi
    info "Installing Ubuntu (proot-distro)... this can take a while."
    out="$(proot-distro install "$DISTRO_NAME" 2>&1)" || {
        if [ -d "$DISTRO_ROOTFS" ] || [ -d "$LEGACY_ROOTFS" ] \
            || proot-distro list 2>&1 | grep -qE "(^|[[:space:]])${DISTRO_NAME}([[:space:]]|$)"; then
            ok "Ubuntu is already installed — continuing."
            return 0
        fi
        err "proot-distro install failed: $out"
        exit 1
    }
}

host_login() {
    local action="${1:-}"; [ $# -gt 0 ] && shift
    install -m 0755 "$SELF_PATH" "$DISTRO_MIRROR"
    proot-distro login "$DISTRO_NAME" --shared-tmp -- env EARTH_INSIDE=1 \
        bash "/tmp/.earth-self.sh" "$action" "$@"
}

ensure_host_pg() {
    command -v pg_ctl >/dev/null 2>&1 || {
        info "Installing native Termux postgresql..."
        pkg update -y >/dev/null 2>&1 || true
        pkg install -y postgresql libandroid-execinfo || return 1
    }
    if ! pg_isready -h 127.0.0.1 -p 5432 -q 2>/dev/null; then
        if [ ! -f "$PG_DATA_HOST/PG_VERSION" ]; then
            info "Initializing Termux-native Postgres data dir..."
            mkdir -p "$PG_DATA_HOST"
            initdb -D "$PG_DATA_HOST" --auth=trust --locale=C || return 1
        fi
        info "Starting Termux-native Postgres on 127.0.0.1:5432..."
        pg_ctl -D "$PG_DATA_HOST" -l "$PG_LOG" \
            -o "-p 5432 -c listen_addresses=127.0.0.1" start || return 1
        sleep 2
    fi
    psql -h 127.0.0.1 -p 5432 -d postgres -c "CREATE ROLE postgres LOGIN SUPERUSER" >/dev/null 2>&1 \
        || psql -h 127.0.0.1 -p 5432 -d postgres -c "ALTER ROLE postgres LOGIN SUPERUSER" >/dev/null 2>&1
    ok "Postgres (Termux-native) is running on 127.0.0.1:5432"
}

stop_host_pg() {
    pg_isready -h 127.0.0.1 -p 5432 -q 2>/dev/null || return 0
    info "Stopping Termux-native Postgres..."
    pg_ctl -D "$PG_DATA_HOST" stop -m fast 2>/dev/null || pkill -x postgres 2>/dev/null || true
    ok "Postgres stopped"
}

do_host_start() {
    ensure_host_pg || { err "Could not start Termux-native Postgres"; exit 1; }
    trap 'echo ""; info "Stopping Termux-native Postgres..."; stop_host_pg' EXIT
    info "Starting Solace (foreground — Ctrl+C stops the server)..."
    host_login _daemon || true
}

do_uninstall() {
    ensure_proot
    ensure_distro
    host_login _uninstall || true
    stop_host_pg
    rm -rf "$HOME/.solace/state" "$PG_DATA_HOST"
    rm -f "$SELF_PATH" "$DISTRO_MIRROR"
    echo "[Solace] Solace has been uninstalled."
}

show_help() {
    echo ""
    echo -e "${c_cyn}"
    echo "   _____       __"
    echo "  / ___/____  / /___ _________"
    echo "  \__ \/ __ \/ / __ \`/ ___/ _ \\"
    echo " ___/ / /_/ / / /_/ / /__/  __/"
    echo "/____/\____/_/\__,_/\___/\___/"
    echo -e "${c_rst}"
    echo "Usage: earth [COMMAND]"
    echo ""
    echo "Commands:"
    echo "  install     Install Solace into proot-distro Ubuntu (first run)"
    echo "  setup       Interactive configuration (.env, OIDC certs, nginx)"
    echo "  start       Start the server in the foreground (live logs; Ctrl+C stops it)"
    echo "  status      Show server status"
    echo "  logs [svc]  Follow a service log (default: web-portal)"
    echo "  update      Update Solace to the latest $RELEASE_TAG build"
    echo "  eula        Accept the Minecraft EULA"
    echo "  eula --delete  Delete the EULA file"
    echo "  uninstall   Completely remove Solace and its data"
    echo "  help        Show this help message"
    echo ""
    echo "Platform: Termux (Android) - proot-distro + Ubuntu | branch: $INSTALL_BRANCH / $RELEASE_TAG"
    echo ""
}

self_update() {
    [ -z "${EARTH_NO_SELF_UPDATE:-}" ] && [ -z "${EARTH_INSIDE:-}" ] || return 0
    command -v curl >/dev/null 2>&1 || return 0
    local tmp
    tmp="$(mktemp "${TMPDIR:-/tmp}/.earth_XXXXXX")"
    curl -fsSL --max-time 10 -o "$tmp" "$RAW/distros/Termux.sh" 2>/dev/null || { rm -f "$tmp"; return 0; }
    [ "$(head -c2 "$tmp")" = "#!" ] || { rm -f "$tmp"; return 0; }
    cmp -s "$tmp" "$SELF_PATH" && { rm -f "$tmp"; return 0; }
    cp -f "$tmp" "$SELF_PATH" 2>/dev/null || { rm -f "$tmp"; return 0; }
    rm -f "$tmp"
    echo "[Solace] Updated to latest ($INSTALL_BRANCH)."
    exec "$BASH" "$SELF_PATH" "$@"
}

main() {
    if [ -n "${EARTH_INSIDE:-}" ]; then
        _distro_main "$@"
        return $?
    fi

    is_termux || { err "Solace for Termux must run inside Termux."; exit 1; }
    ensure_proot

    local cmd="${1:-}"; [ $# -gt 0 ] && shift
    case "$cmd" in
        install)   ensure_distro; host_login _install ;;
        setup)     ensure_distro; host_login _setup ;;
        start)     ensure_distro; do_host_start ;;
        status)    ensure_distro; host_login _status ;;
        logs)      ensure_distro; host_login _logs "${1:-web-portal}" ;;
        update)    ensure_distro; host_login _update ;;
        eula)      ensure_distro; host_login _eula "$@" ;;
        uninstall) do_uninstall ;;
        help|-h|--help) show_help ;;
        "")        ensure_distro; host_login _status ;;
        *) warn "Unknown command: $cmd"; show_help; exit 1 ;;
    esac
}

if [ -z "${EARTH_INSIDE:-}" ] && [ "${EARTH_NO_SELF_UPDATE:-0}" = "0" ]; then
    self_update "$@"
fi
main "$@"