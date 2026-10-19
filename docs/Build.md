# Building

## OS

* Linux or Windows (WSL is recommended)

## Dependencies

* [.NET 11](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)
* [Aspire](https://aspire.dev/get-started/install-cli/)
* Java 21
* Docker

## Local Setup

1) If you have not cloned with submodules, make sure to run `git submodule update --init --recursive`
2) Obtain the resourcepack from [this URL](https://cdn.mceserv.net/availableresourcepack/resourcepacks/dba38e59-091a-4826-b76a-a08d7de5a9e2-1301b0c257a311678123b9e7325d0d6c61db3c35), using Wayback Machine
3) Rename it to `vanilla.zip` and put it into `staticdata/resourcepacks`
4) Navigate to `src/Solace.AppHost`
5) Modify `appsettings.Development.json`
    * **Required**
    * Set `Shared/AcceptMinecraftEula` to `true`
    * Change `Shared/PublicEndpoints`. Replace `PC_ENDPOINT` with an IP or hostname. *(Note the PC must be able to reach itself through the endpoint, otherwise in-game sign in will fail.)*

    ``` json
    "PublicEndpoints": {
        "WebPortal": "http://PC_ENDPOINT:5000",
        "Locator": "http://PC_ENDPOINT:8080",
        "AuthServer": "http://PC_ENDPOINT:8088",
        "ApiServer": "http://PC_ENDPOINT:8089",
        "Cdn": "http://PC_ENDPOINT:8090"
    },
    ```

    * Change `BuildplateLauncher/PublicEndpoint` to an IP or hostname, without protocol or port
6) Run `dotnet run`
7) The admin account email and password for web portal will be shown in the web portal logs. If forgotten, reset them by setting `WebPortal/AdminAccountPassword` in `appsettings.Development.json`

## Termux (Android) source builds

The `install.sh` script automates everything below. Use this section when you want to
build or debug the AppHost manually inside proot-distro Ubuntu.

The `earth` TUI and PostgreSQL run in Termux and use `fzf`, `pgrep`, `pkill` and the
`postgresql` package; install them with `pkg install fzf procps postgresql`. `install.sh`
does this automatically.

1. Install Termux from F-Droid, then install the proot helper:

   ```bash
   pkg install proot-distro
   proot-distro install ubuntu
   proot-distro login ubuntu
   ```

2. Inside Ubuntu, install the native dependencies:

   ```bash
   apt-get update
   apt-get install -y ca-certificates curl wget git unzip fzf openssl \
       openjdk-21-jre-headless libicu-dev \
       procps psmisc iproute2 tzdata libfontconfig1 libfreetype6
   ```

3. Install the .NET 11 SDK and Aspire CLI:

   ```bash
   curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
   bash /tmp/dotnet-install.sh --channel 11.0 --install-dir "$HOME/.dotnet"
   export DOTNET_ROOT="$HOME/.dotnet"
   export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
   dotnet tool install -g Aspire.Cli --version 13.5.4
   ```

4. Set up PostgreSQL in Termux (proot cannot run database clusters, so the database
   lives outside Ubuntu):

   ```bash
   pkg install postgresql
   PGDATA="$PREFIX/var/lib/postgresql"
   printf 'change-me\n' > "$PREFIX/tmp/pg-pw"
   initdb -D "$PGDATA" -U solace -E UTF8 --locale=C.UTF-8 \
       --auth-local=trust --auth-host=scram-sha-256 --pwfile="$PREFIX/tmp/pg-pw"
   rm "$PREFIX/tmp/pg-pw"
   printf '\nunix_socket_directories = %s\n' "'$PREFIX/tmp'" >> "$PGDATA/postgresql.conf"
   pg_ctl -D "$PGDATA" -w start
   for db in EarthDb PlayfabDb WebPortalDb; do
       PGPASSWORD=change-me createdb -h 127.0.0.1 -U solace -O solace "$db"
   done
   ```

   The services inside Ubuntu reach this server on `127.0.0.1:5432`.

5. Clone the repository and fetch the submodules:

   ```bash
   git clone --branch aspire --recurse-submodules \
       https://github.com/Earth-Restored/Solace.git "$HOME/Solace"
   ```

6. Export the Termux configuration. `Termux__Enabled=true` tells the AppHost to skip
   Docker Compose, pgAdmin and nginx, and to use the connection strings below:

   ```bash
   export Termux__Enabled=true
   export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true
   export ConnectionStrings__EarthDb='Host=127.0.0.1;Port=5432;Database=EarthDb;Username=solace;Password=change-me'
   export ConnectionStrings__PlayfabDb='Host=127.0.0.1;Port=5432;Database=PlayfabDb;Username=solace;Password=change-me'
   export ConnectionStrings__WebPortalDb='Host=127.0.0.1;Port=5432;Database=WebPortalDb;Username=solace;Password=change-me'
   export Shared__StaticDataPath="$HOME/Solace/staticdata"
   export ObjectStore__DataDirectory="$HOME/Solace/data/object_store"
   export Shared__PublicEndpoints__WebPortal=http://127.0.0.1:5000
   export Shared__PublicEndpoints__Locator=http://127.0.0.1:8080
   export Shared__PublicEndpoints__AuthServer=http://127.0.0.1:8088
   export Shared__PublicEndpoints__ApiServer=http://127.0.0.1:8089
   export Shared__PublicEndpoints__Cdn=http://127.0.0.1:8090
   ```

   The generated `~/.config/solace/termux.env` from `install.sh` contains the full set of
   values, including a random OIDC client secret.

7. Run the AppHost:

   ```bash
   cd "$HOME/Solace/src/Solace.AppHost"
   aspire run -- --environment Termux
   ```

   The AppHost launch profile normally forces `Development`. When `Termux__Enabled=true`
   is set, `AppHost.cs` switches the environment to `Termux` before the builder is created
   and uses HTTP dashboard endpoints, so `appsettings.Termux.json` is loaded even when
   Aspire starts the project through its launch profile.

   This manual command runs in the current proot session, so the server stops when that
   session exits. The installed `earth` launcher avoids this by keeping a separate
   long-lived proot session for the server.

8. The resource pack is required for buildplate previews and some static data. Put it at
   `staticdata/resourcepacks/vanilla.zip`.

## Publishing

1) Navigate to `scripts`
2) Run `upload-docker-registry.ps1`, specify your username and optionally, the image registry and which projects/architectures to upload
3) Navigate to `src/Solace.AppHost`
4) Run `aspire publish -- --PostgresVolumeName="solace.prod-postgres-data"`, this generates `docker-compose.yml` and `.env` inside `aspire-output`.
5) Copy the `staticdata` folder to `src/Solace.AppHost/aspire-output`
6) Run `dotnet set-env-file-defaults.cs ./aspire-output/.env` *(Add -o to overwrite existing values.)*
7) Copy the contents of `template` to `aspire-output`
8) Publish Solace.Db.Migrator, copy outputs to aspire-output/migrator

### Running published containers

1) Run the setup script `dotnet setup.cs`
2) Run either `.\up.ps1` or `./up.sh` depending on your OS
