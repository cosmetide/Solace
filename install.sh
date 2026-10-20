#!/usr/bin/env bash

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
ORG='\033[38;5;208m'
CYN='\033[1;36m'
BLU='\033[1;34m'
RST='\033[0m'

banner() {
    echo -e "${BLU}"
    echo "     _____       __"
    echo "    / ___/____  / /___ _________"
    echo "    \__ \/ __ \/ / __ \`/ ___/ _ \\"
    echo "   ___/ / /_/ / / /_/ / /__/  __/"
    echo "  /____/\____/_/\__,_/\___/\___/"
    echo -e "${RST}"
}

help_text() {
    echo ""
    echo -e "${CYN}Usage:${RST} install.sh [OPTIONS]"
    echo ""
    echo "Install Solace - a Minecraft Earth replacement server."
    echo ""
    echo -e "${CYN}Options:${RST}"
    echo "  -h, --help     Show this help message"
    echo "  --source       Build from source on this device"
    echo "  --prebuilt     Require a prebuilt bundle"
    echo ""
    echo -e "${CYN}Environment:${RST}"
    echo "  SOLACE_REPO      GitHub repository (default: cosmetide/Solace)"
    echo "  SOLACE_BRANCH    Branch to install (default: main)"
    echo "  SOLACE_INSTALL_MODE     auto, prebuilt, or source (default: auto)"
    echo "  SOLACE_PREBUILT_URL     URL to a termux-aspire.zip bundle"
    echo "  SOLACE_PREBUILT_SHA256  Optional SHA-256 checksum for the bundle"
    echo ""
    echo -e "${CYN}Platforms:${RST}"
    echo "  Termux (Android)   Uses proot-distro + Ubuntu"
    echo ""
    echo "For Docker-based Linux/macOS installs, see docs/Installation.md."
    echo "After installation, run: earth"
    exit 0
}

INSTALL_MODE="${SOLACE_INSTALL_MODE:-auto}"

for arg in "$@"; do
    case "$arg" in
        -h|--help) help_text ;;
        --source) INSTALL_MODE=source ;;
        --prebuilt) INSTALL_MODE=prebuilt ;;
    esac
done

print_step() {
    echo ""
    echo -e "${CYN}========================================${RST}"
    echo -e "${CYN}  $1${RST}"
    echo -e "${CYN}========================================${RST}"
}

print_sub() {
    echo -e "  ${BLU}>${RST} $1"
}

ok()   { echo -e "${GRN}[OK] $1${RST}"; }
skip() { echo -e "${YLW}[SKIP] $1${RST}"; }
err()  { echo -e "${RED}[ERROR] $1${RST}"; exit 1; }

GITHUB_REPO="${SOLACE_REPO:-cosmetide/Solace}"
GITHUB_BRANCH="${SOLACE_BRANCH:-main}"
GITHUB_URL="${SOLACE_REPO_URL:-https://github.com/$GITHUB_REPO.git}"
ASPIRE_CLI_VERSION="${ASPIRE_CLI_VERSION:-13.5.4}"

case "$INSTALL_MODE" in
    auto|prebuilt|source) ;;
    *) err "SOLACE_INSTALL_MODE must be auto, prebuilt, or source" ;;
esac

if [ "$INSTALL_MODE" = source ]; then
    PREBUILT_URL=""
    PREBUILT_URL_IS_DEFAULT=0
elif [ -n "${SOLACE_PREBUILT_URL:-}" ]; then
    PREBUILT_URL="$SOLACE_PREBUILT_URL"
    PREBUILT_URL_IS_DEFAULT=0
else
    PREBUILT_URL="https://github.com/$GITHUB_REPO/releases/latest/download/termux-aspire.zip"
    PREBUILT_URL_IS_DEFAULT=1
fi

clear && banner

if [ -n "$TERMUX_VERSION" ] || echo "$PREFIX" | grep -q "com.termux"; then
    export DEBIAN_FRONTEND=noninteractive
    dpkg --configure -a >/dev/null 2>&1 || true

    print_step "1. CHECKING TERMUX PACKAGES"
    command -v fzf >/dev/null 2>&1 || pkg install -y fzf || err "Failed to install fzf"
    command -v pgrep >/dev/null 2>&1 || pkg install -y procps || err "Failed to install procps"
    command -v curl >/dev/null 2>&1 || pkg install -y curl || err "Failed to install curl"
    command -v ifconfig >/dev/null 2>&1 || pkg install -y net-tools || true
    command -v socat >/dev/null 2>&1 || pkg install -y socat || true
    command -v termux-open-url >/dev/null 2>&1 || pkg install -y termux-tools || true
    command -v initdb >/dev/null 2>&1 || pkg install -y postgresql || err "Failed to install postgresql"
    hash -r
    ok "Termux packages installed"

    print_step "2. CHECKING PROOT-DISTRO"
    if ! command -v proot-distro >/dev/null 2>&1; then
        pkg update -y || true
        pkg install -y -o Dpkg::Options::="--force-confnew" proot-distro || {
            dpkg --configure -a
            pkg install -y -o Dpkg::Options::="--force-confnew" proot-distro
        } || err "proot-distro install failed"
        hash -r
        command -v proot-distro >/dev/null || err "proot-distro install failed"
        ok "Installed proot-distro"
    else
        skip "Already installed"
    fi

    print_step "3. CHECKING UBUNTU"
    if proot-distro login ubuntu -- true 2>/dev/null; then
        skip "Ubuntu already installed"
    else
        proot-distro install ubuntu || err "Ubuntu install failed"
        ok "Ubuntu installed"
    fi

    print_step "4. CONFIGURING POSTGRESQL"
    echo -e "  ${CYN}PostgreSQL runs in Termux itself; proot-distro cannot"
    echo -e "  create or run database clusters.${RST}"
    TERMUX_PGDATA="$PREFIX/var/lib/postgresql"

    TERMUX_ENV_FILE="$HOME/.config/solace/termux.env"
    OLD_DB_PASSWORD="$(grep -E '^SOLACE_DB_PASSWORD=' "$TERMUX_ENV_FILE" 2>/dev/null | tail -n1 | cut -d= -f2- | tr -d "'\"")"
    DB_PASSWORD="${OLD_DB_PASSWORD:-$(dd if=/dev/urandom bs=16 count=1 2>/dev/null | od -An -tx1 | tr -d ' \n' | cut -c1-32)}"

    if [ -f "$TERMUX_PGDATA/PG_VERSION" ]; then
        skip "PostgreSQL cluster already exists"
    else
        echo -e "  ${CYN}Initializing the PostgreSQL cluster (this can take a few minutes).${RST}"
        PWFILE="$PREFIX/tmp/solace-pg-pw.$$"
        (umask 077; printf '%s\n' "$DB_PASSWORD" > "$PWFILE")
        initdb -D "$TERMUX_PGDATA" -U solace -E UTF8 --locale=C.UTF-8 \
            --auth-local=trust --auth-host=scram-sha-256 --pwfile="$PWFILE" >/dev/null \
            || { rm -f "$PWFILE"; err "PostgreSQL initdb failed"; }
        rm -f "$PWFILE"
        printf '\nunix_socket_directories = %s\n' "'$PREFIX/tmp'" >> "$TERMUX_PGDATA/postgresql.conf"
        touch "$TERMUX_PGDATA/.solace-managed"
        ok "PostgreSQL cluster created"
    fi

    pg_ctl -D "$TERMUX_PGDATA" -l "$TERMUX_PGDATA/server.log" -w -t 300 start >/dev/null 2>&1 || true
    for _ in $(seq 1 120); do
        pg_isready -q -h 127.0.0.1 -p 5432 && break
        sleep 1
    done
    pg_isready -q -h 127.0.0.1 -p 5432 || err "PostgreSQL failed to start. See $TERMUX_PGDATA/server.log"

    PG_SOCKET="$PREFIX/tmp"
    if ! psql -h "$PG_SOCKET" -U solace -d postgres -tAc "SELECT 1" >/dev/null 2>&1; then
        psql -h "$PG_SOCKET" -d postgres -v ON_ERROR_STOP=1 \
            -c "CREATE ROLE solace WITH LOGIN SUPERUSER;" >/dev/null \
            || err "Failed to create the solace database role"
    fi
    psql -h "$PG_SOCKET" -U solace -d postgres -v ON_ERROR_STOP=1 \
        -c "ALTER ROLE solace WITH LOGIN SUPERUSER PASSWORD '$DB_PASSWORD';" >/dev/null \
        || err "Failed to configure the solace database role"
    for db in EarthDb PlayfabDb WebPortalDb; do
        if ! psql -h "$PG_SOCKET" -U solace -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$db'" | grep -q 1; then
            createdb -h "$PG_SOCKET" -U solace -O solace "$db" >/dev/null \
                || err "Failed to create database $db"
        fi
    done
    pg_ctl -D "$TERMUX_PGDATA" -w -t 60 stop -m fast >/dev/null 2>&1 || true
    ok "PostgreSQL configured"

    print_step "5. CONFIGURING UBUNTU"
    if [ -n "$PREBUILT_URL" ]; then
        echo -e "  ${CYN}This step installs .NET 11, Java 21 and a prebuilt"
        echo -e "  Solace bundle. The prebuilt bundle is recommended because"
        echo -e "  it avoids a full compile on the phone.${RST}"
    else
        echo -e "  ${CYN}This step installs .NET 11 and Java 21, then builds"
        echo -e "  Solace from source. This can take 30-90 minutes on a phone.${RST}"
    fi

    if ! proot-distro login ubuntu -- env \
        GITHUB_REPO="$GITHUB_REPO" \
        GITHUB_URL="$GITHUB_URL" \
        GITHUB_BRANCH="$GITHUB_BRANCH" \
        ASPIRE_CLI_VERSION="$ASPIRE_CLI_VERSION" \
        DB_PASSWORD="$DB_PASSWORD" \
        SOLACE_INSTALL_MODE="$INSTALL_MODE" \
        SOLACE_PREBUILT_URL="$PREBUILT_URL" \
        SOLACE_PREBUILT_URL_IS_DEFAULT="$PREBUILT_URL_IS_DEFAULT" \
        SOLACE_PREBUILT_SHA256="${SOLACE_PREBUILT_SHA256:-}" \
        bash -s <<'INNER_SETUP'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

SOLACE_DIR="$HOME/Solace"
CONFIG_DIR="$HOME/.config/solace"
ENV_FILE="$CONFIG_DIR/termux.env"
SETTINGS_FILE="$CONFIG_DIR/termux-settings.json"

echo "  [1/7] Updating Ubuntu packages"
apt-get update -qq

echo "  [2/7] Installing system dependencies"
apt-get install -y -qq \
    ca-certificates curl wget git unzip fzf openssl \
    openjdk-21-jre-headless libicu-dev \
    procps psmisc iproute2 tzdata \
    libfontconfig1 libfreetype6 >/dev/null

echo "  [3/7] Installing .NET 11 SDK"
install_dotnet() {
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    if ! bash /tmp/dotnet-install.sh --channel 11.0 --install-dir "$HOME/.dotnet" --no-path; then
        echo "  [3/7] Default channel failed, retrying with the preview channel"
        bash /tmp/dotnet-install.sh --channel 11.0 --quality preview --install-dir "$HOME/.dotnet" --no-path
    fi
    rm -f /tmp/dotnet-install.sh
}

if [ ! -x "$HOME/.dotnet/dotnet" ] || ! "$HOME/.dotnet/dotnet" --list-sdks 2>/dev/null | grep -q '^11\.'; then
    install_dotnet
fi
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
"$HOME/.dotnet/dotnet" --list-sdks | grep -q '^11\.' || {
    echo "The .NET 11 SDK was not installed correctly." >&2
    exit 1
}

echo "  [4/7] Installing Aspire CLI"
install_aspire_cli() {
    local attempt
    for attempt in 1 2 3; do
        if dotnet tool update -g Aspire.Cli --version "$ASPIRE_CLI_VERSION" >/dev/null 2>&1; then
            return 0
        fi
        if dotnet tool install -g Aspire.Cli --version "$ASPIRE_CLI_VERSION" >/dev/null 2>&1; then
            return 0
        fi
        sleep 3
    done
    return 1
}

install_aspire_cli || {
    echo "The Aspire CLI could not be installed." >&2
    exit 1
}
hash -r
command -v aspire >/dev/null 2>&1 && aspire --version >/dev/null 2>&1 || {
    echo "The Aspire CLI is not available on PATH." >&2
    exit 1
}

if ! grep -q 'DOTNET_ROOT' "$HOME/.bashrc" 2>/dev/null; then
    cat >> "$HOME/.bashrc" <<'BASHRC'

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_gcServer=0
BASHRC
fi
sed -i '/DOTNET_GCHeapHardLimit/d' "$HOME/.bashrc" 2>/dev/null || true

echo "  [5/7] Fetching Solace ($GITHUB_BRANCH)"
if [ -d "$SOLACE_DIR/.git" ]; then
    git config --global --add safe.directory "$SOLACE_DIR" 2>/dev/null || true
    git -C "$SOLACE_DIR" fetch --prune origin "$GITHUB_BRANCH"
    if ! git -C "$SOLACE_DIR" checkout "$GITHUB_BRANCH" 2>/dev/null; then
        git -C "$SOLACE_DIR" checkout -b "$GITHUB_BRANCH" "origin/$GITHUB_BRANCH"
    fi
    if ! git -C "$SOLACE_DIR" merge --ff-only "origin/$GITHUB_BRANCH"; then
        echo "  [5/7] Warning: local changes prevent a fast-forward. Keeping the existing checkout."
    fi
else
    rm -rf "$SOLACE_DIR"
    git clone --branch "$GITHUB_BRANCH" --recurse-submodules "$GITHUB_URL" "$SOLACE_DIR"
fi
git -C "$SOLACE_DIR" config --add safe.directory "$SOLACE_DIR" 2>/dev/null || true
git -C "$SOLACE_DIR" submodule update --init --recursive

rm -f "$SOLACE_DIR/.termux-prebuilt"
if [ "$SOLACE_INSTALL_MODE" != "source" ] && [ -n "${SOLACE_PREBUILT_URL:-}" ]; then
    echo "  [5/7] Checking prebuilt Aspire bundle"
    PREBUILT_ZIP="/tmp/termux-aspire.zip"
    if curl -fL --retry 3 --connect-timeout 30 "$SOLACE_PREBUILT_URL" -o "$PREBUILT_ZIP"; then
        if [ -n "${SOLACE_PREBUILT_SHA256:-}" ]; then
            printf '%s  %s\n' "$SOLACE_PREBUILT_SHA256" "$PREBUILT_ZIP" | sha256sum -c - || {
                rm -f "$PREBUILT_ZIP"
                echo "The prebuilt bundle checksum did not match." >&2
                exit 1
            }
        fi
        unzip -oq "$PREBUILT_ZIP" -d "$SOLACE_DIR"
        rm -f "$PREBUILT_ZIP"
        if [ -f "$SOLACE_DIR/termux-aspire.zip" ] && [ ! -f "$SOLACE_DIR/src/Solace.AppHost/bin/Debug/net11.0/Solace.AppHost" ]; then
            unzip -oq "$SOLACE_DIR/termux-aspire.zip" -d "$SOLACE_DIR"
            rm -f "$SOLACE_DIR/termux-aspire.zip" "$SOLACE_DIR/termux-aspire.zip.sha256"
        fi
        if [ -d "$SOLACE_DIR/termux-aspire" ]; then
            cp -a "$SOLACE_DIR/termux-aspire/." "$SOLACE_DIR/"
            rm -rf "$SOLACE_DIR/termux-aspire"
        fi
        APP_HOST_BIN="$SOLACE_DIR/src/Solace.AppHost/bin/Debug/net11.0/Solace.AppHost"
        if [ ! -f "$APP_HOST_BIN" ]; then
            echo "The prebuilt bundle is missing the AppHost binary." >&2
            exit 1
        fi
        chmod +x "$APP_HOST_BIN"
        touch "$SOLACE_DIR/.termux-prebuilt"
        echo "  [5/7] Installed prebuilt Aspire bundle"
    else
        rm -f "$PREBUILT_ZIP"
        if [ "$SOLACE_INSTALL_MODE" = "prebuilt" ] || [ "${SOLACE_PREBUILT_URL_IS_DEFAULT:-0}" != "1" ]; then
            echo "The prebuilt bundle could not be downloaded." >&2
            exit 1
        fi
        echo "  [5/7] No prebuilt release found; building from source"
    fi
fi

echo "  [6/7] Writing Termux configuration"
mkdir -p "$CONFIG_DIR"

OLD_EULA="$(grep -E '^Shared__AcceptMinecraftEula=' "$ENV_FILE" 2>/dev/null | tail -n1 | cut -d= -f2- | tr -d "'\"" || true)"
OLD_PUBLIC_IP="$(grep -E '^SOLACE_PUBLIC_IP=' "$ENV_FILE" 2>/dev/null | tail -n1 | cut -d= -f2- | tr -d "'\"" || true)"
EULA_VALUE="${OLD_EULA:-false}"

detect_public_ip() {
    local ip
    ip="$(ifconfig wlan0 2>/dev/null | awk '/inet /{print $2; exit}')"
    [ -n "$ip" ] || ip="$(getprop dhcp.wlan0.ipaddress 2>/dev/null | head -n1)"
    [ -n "$ip" ] || ip="$(ip route get 1.1.1.1 2>/dev/null | awk '{print $7; exit}')"
    [ -n "$ip" ] || ip="127.0.0.1"
    printf '%s\n' "$ip"
}

PUBLIC_IP_VALUE="${OLD_PUBLIC_IP:-$(detect_public_ip)}"

if [ -f "$ENV_FILE" ]; then
    cp "$ENV_FILE" "$ENV_FILE.bak"
fi

OIDC_SECRET="$(openssl rand -hex 24)"

write_env() {
    printf '%s=%q\n' "$1" "$2" >> "$ENV_FILE"
}

cat > "$ENV_FILE" <<'EOF'
EOF
write_env ASPNETCORE_ENVIRONMENT Termux
write_env DOTNET_ENVIRONMENT Termux
write_env Termux__Enabled true
write_env ASPIRE_ALLOW_UNSECURED_TRANSPORT true
echo "" >> "$ENV_FILE"

write_env SOLACE_DB_PASSWORD "$DB_PASSWORD"
write_env ConnectionStrings__EarthDb "Host=127.0.0.1;Port=5432;Database=EarthDb;Username=solace;Password=$DB_PASSWORD"
write_env ConnectionStrings__PlayfabDb "Host=127.0.0.1;Port=5432;Database=PlayfabDb;Username=solace;Password=$DB_PASSWORD"
write_env ConnectionStrings__WebPortalDb "Host=127.0.0.1;Port=5432;Database=WebPortalDb;Username=solace;Password=$DB_PASSWORD"
echo "" >> "$ENV_FILE"

write_env Shared__AcceptMinecraftEula "$EULA_VALUE"
write_env Shared__FixUpBuildplatesOnImport false
write_env Shared__StaticDataPath "$SOLACE_DIR/staticdata"
write_env Shared__ResolvePaths true
write_env Shared__Captcha__Provider NoOp
write_env Shared__PublicEndpoints__WebPortal "http://$PUBLIC_IP_VALUE:5000"
write_env Shared__PublicEndpoints__Locator "http://$PUBLIC_IP_VALUE:8080"
write_env Shared__PublicEndpoints__AuthServer "http://$PUBLIC_IP_VALUE:8088"
write_env Shared__PublicEndpoints__ApiServer "http://$PUBLIC_IP_VALUE:8089"
write_env Shared__PublicEndpoints__Cdn "http://$PUBLIC_IP_VALUE:8090"
write_env Shared__Oidc__WebPortal__AuthServer__ClientSecret "$OIDC_SECRET"
write_env Shared__Oidc__WebPortal__AuthServer__AllowInsecure true
echo "" >> "$ENV_FILE"

write_env ObjectStore__DataDirectory "$SOLACE_DIR/data/object_store"
write_env Locator__Port 8080
write_env AuthServer__Port 8088
write_env Cdn__Port 8090
write_env ApiServer__Port 8089
write_env ApiServer__Authentication__LocalLoginOnly true
write_env WebPortal__Port 5000
write_env WebPortal__BuildplatePreview__Enabled true
write_env WebPortal__BuildplatePreview__GenerationMaxConcurrency 1
echo "" >> "$ENV_FILE"

write_env BuildplateLauncher__PublicEndpoint "$PUBLIC_IP_VALUE"
write_env BuildplateLauncher__BaseInstancePublicPort 19132
write_env BuildplateLauncher__FountainBridgeJarName 'fountain-{{version}}-SNAPSHOT-jar-with-dependencies.jar'
write_env BuildplateLauncher__FabricJarName 'server-{{version}}.jar'
write_env BuildplateLauncher__ConnectorPluginJarName 'buildplate-connector-plugin-{{version}}-SNAPSHOT-jar-with-dependencies.jar'
echo "" >> "$ENV_FILE"

write_env SOLACE_PUBLIC_IP "$PUBLIC_IP_VALUE"
write_env SOLACE_DASHBOARD_PORT 8888
write_env SOLACE_REPO "$GITHUB_REPO"
write_env SOLACE_BRANCH "$GITHUB_BRANCH"
chmod 600 "$ENV_FILE"

if [ -f "$SOLACE_DIR/.termux-prebuilt" ]; then
    INSTALL_MODE_USED="prebuilt"
else
    INSTALL_MODE_USED="source"
fi

cat > "$SETTINGS_FILE" <<EOF
{
  "installMode": "$INSTALL_MODE_USED",
  "branch": "$GITHUB_BRANCH",
  "version": "$(git -C "$SOLACE_DIR" rev-parse --short HEAD)",
  "updatedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF
chmod 600 "$SETTINGS_FILE"

echo "  [7/7] Preparing static data"
mkdir -p "$SOLACE_DIR/staticdata/resourcepacks"
mkdir -p "$SOLACE_DIR/data/object_store"

echo ""
echo "  [DONE] Ubuntu configured."
INNER_SETUP
    then
        err "Ubuntu setup failed. Fix the error above and re-run install.sh."
    fi

    ok "Ubuntu configured"

    print_step "6. CREATING EARTH COMMAND"
    mkdir -p "$PREFIX/bin"
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-}")" 2>/dev/null && pwd || true)"
    if [ -n "$SCRIPT_DIR" ] && [ -f "$SCRIPT_DIR/distros/Termux.sh" ]; then
        cp "$SCRIPT_DIR/distros/Termux.sh" "$PREFIX/bin/earth"
    else
        curl -fsSL "https://raw.githubusercontent.com/$GITHUB_REPO/refs/heads/$GITHUB_BRANCH/distros/Termux.sh" -o "$PREFIX/bin/earth" \
            || err "Failed to download the earth launcher"
    fi
    chmod +x "$PREFIX/bin/earth"
    ok "earth command installed"

    echo ""
    echo -e "${GRN}========================================${RST}"
    echo -e "${ORG}           INSTALL COMPLETE             ${RST}"
    echo -e "${GRN}========================================${RST}"
    echo ""
    if [ -f "$PREFIX/var/lib/proot-distro/containers/ubuntu/rootfs/root/Solace/.termux-prebuilt" ]; then
        INSTALLED_MODE="prebuilt"
    else
        INSTALLED_MODE="source"
    fi
    echo -e "  ${CYN}OS:${RST}      Termux (proot-distro ubuntu)"
    echo -e "  ${CYN}Arch:${RST}    $(uname -m)"
    echo -e "  ${CYN}Mode:${RST}    $INSTALLED_MODE"
    echo -e "  ${CYN}Branch:${RST}  $GITHUB_BRANCH"
    echo -e "  ${CYN}Server:${RST}  ~/Solace (inside Ubuntu)"
    echo -e "  ${CYN}Portal:${RST}  http://$PUBLIC_IP_VALUE:5000"
    echo -e "  ${CYN}Aspire:${RST}  http://$PUBLIC_IP_VALUE:8888"
    echo ""
    echo -e "${CYN}Next steps:${RST}"
    echo "  1. Download the resource pack and save it as:"
    echo "     ~/Solace/staticdata/resourcepacks/vanilla.zip (inside Ubuntu)"
    echo "  2. Run: earth"
    echo "  3. Accept the Minecraft EULA from the TUI"
    echo "  4. Start the server, then choose Launch Web Portal or run: earth portal"
    echo "  5. Check Settings > Show Admin Account for the initial password"
    echo ""
    echo -e "${CYN}Useful commands:${RST}"
    echo "  earth              TUI menu"
    echo "  earth portal       open the Solace web portal"
    echo "  earth aspire       open the Aspire panel"
    echo "  earth eula         accept the Minecraft EULA"
    echo "  earth uninstall    remove Solace completely"
    echo ""
    exit 0
fi

echo -e "${YLW}This installer currently supports Termux (Android) only.${RST}"
echo ""
echo "For Linux, macOS and Windows, use the Docker-based setup:"
echo "  https://github.com/$GITHUB_REPO/blob/$GITHUB_BRANCH/docs/Installation.md"
echo ""
echo "On a Termux device, run:"
echo "  curl -fsSL https://raw.githubusercontent.com/$GITHUB_REPO/refs/heads/$GITHUB_BRANCH/install.sh | bash"
echo ""
exit 0
