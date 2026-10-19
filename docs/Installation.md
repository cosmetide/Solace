# Installation

Solace can run with Docker on a desktop/server, or natively inside Ubuntu on Android
using Termux. The Termux setup does not use Docker: PostgreSQL, the Aspire AppHost and
the Solace services all run directly inside a proot-distro Ubuntu environment.

## Termux (Android)

### Requirements

* **Termux** installed from [F-Droid](https://f-droid.org/packages/com.termux/) (the Play Store build is outdated)
* Android 8 or newer, arm64 device
* About 10 GB of free storage for the Ubuntu rootfs, .NET SDK, NuGet packages and the Solace build
* A stable Wi-Fi connection for the first install (it downloads .NET, Java, PostgreSQL and NuGet packages)
* The Minecraft Earth resource pack (`vanilla.zip`, see [Build.md](Build.md))

### Install

Run this from Termux:

```bash
curl -fsSL https://raw.githubusercontent.com/Earth-Restored/Solace/refs/heads/aspire/install.sh | bash
```

The installer:

1. Installs `proot-distro` and an Ubuntu rootfs.
2. Installs the .NET 11 SDK, the Aspire CLI, Java 21 and PostgreSQL inside Ubuntu.
3. Clones the `aspire` branch to `~/Solace` inside Ubuntu.
4. Creates the `solace` database role and the `EarthDb`, `PlayfabDb` and `WebPortalDb` databases.
5. Writes the runtime configuration to `~/.config/solace/termux.env`.
6. Installs the `earth` launcher in Termux.

The first install can take 10-30 minutes on a phone.

### First start

1. Put the resource pack at `~/Solace/staticdata/resourcepacks/vanilla.zip` **inside Ubuntu**.
   From Termux you can copy it in with:

   ```bash
   proot-distro login ubuntu -- mkdir -p /root/Solace/staticdata/resourcepacks
   proot-distro login ubuntu -- cp /sdcard/Download/vanilla.zip /root/Solace/staticdata/resourcepacks/vanilla.zip
   ```

   (Grant Termux storage access with `termux-setup-storage` first if needed.)
2. Run:

   ```bash
   earth
   ```

3. Select **Start/Stop Server** and accept the Minecraft EULA when prompted.
4. Wait for the first build to finish. The first start compiles the whole solution and can
   take a long time on a phone; subsequent starts are much faster.
5. Open the admin panel at <http://127.0.0.1:5000>.
6. The initial admin account is `admin@solace.com`. Open **Settings > Show Admin Account**
   in the `earth` menu to read the generated password. Change it after the first login.

The `earth` menu runs in Termux, while the server runs in its own background proot session.
Exiting the menu or closing the terminal window does not stop the server; use
**Start/Stop Server** in the menu (or `earth stop`) to stop it. Keep the Termux app alive
(and exempt it from Android battery optimization) while the server is running.

### Playing from another device

By default the advertised endpoints use `127.0.0.1`, which only works on the phone itself.
The services listen on the network, but the URLs shown in the portal and used by the game
must point at an address other devices can reach. To play from a PC or another phone on the
same network:

1. Find the Android device's LAN IP address (for example `192.168.1.50`).
2. In the `earth` menu, open **Settings > Change Public IP** and enter that address.
3. Restart the server.
4. Use the same address in the Minecraft Earth patcher.

The individual services listen on these ports; no nginx reverse proxy is used:

| Service        | Port  |
|----------------|-------|
| Web Portal     | 5000  |
| Locator        | 8080  |
| Auth Server    | 8088  |
| API Server     | 8089  |
| CDN            | 8090  |
| Buildplates    | 19132 and up (TCP and UDP) |

Make sure Android's battery optimization does not kill Termux while the server is running.

### Updating and uninstalling

* **Update:** run `earth`, open **Update Solace**, and choose the `aspire` branch.
* **Stop:** run `earth` and select **Start/Stop Server**.
* **Uninstall:** run `earth uninstall` from Termux. This removes the Ubuntu-side files,
  the databases, the configuration and the `earth` command. The proot-distro Ubuntu
  installation itself is left in place.

### Troubleshooting

* Read the AppHost output from **Logs > Startup output** in the `earth` menu.
* PostgreSQL logs are in `/var/log/postgresql/` inside Ubuntu.
* If the admin password cannot be found, run this inside Ubuntu:

  ```bash
  proot-distro login ubuntu -- bash -lc \
    'export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH; \
     aspire logs web-portal --apphost $HOME/Solace/src/Solace.AppHost/Solace.AppHost.csproj --tail 2000'
  ```

## Docker (Linux, macOS, Windows)

### Dependencies

* [Docker](https://www.docker.com/products/docker-desktop/)
* [.NET 11](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)

### Setup Prerequisites

You will need your local IP address so the patched app can connect to your server.

* **Windows:** Settings > Network & Internet > Wi-Fi (or Ethernet) > Wi-Fi properties > Find **IPv4 address**
* **Linux:** Run `ip address`, `hostname -I`, or `ifconfig -a` in terminal
* **macOS:** System Settings > Network > select your active connection > Details > Find **IP Address** (or run `ipconfig getifaddr en0` in terminal)

*Note: Your local IP address will typically start with `192.168.x.x`, `10.x.x.x`, or `172.16.x.x`.*

### Server Setup

1. Download the latest release from [Releases](https://github.com/Earth-Restored/Solace/releases).
2. Extract the ZIP file. *(Note for Windows users: Avoid extracting to a OneDrive-backed folder).*
3. Open a terminal inside the extracted directory.
4. Run the setup script: `dotnet setup.cs`
5. If you expect iOS clients, change `Authentication__LocalLoginOnly` inside `docker-compose.yaml` to `"False"`, note that this makes the server insecure
6. Start the server by running `.\up.ps1` (Windows) or `./up.sh` (Linux/macOS).
7. Check logs with `docker compose logs web-portal`. Scroll up until you see `"SETUP: Initial owner account created!"` along with the admin email and password.
   * *If you forget these credentials later, reset them by setting `WEBPORTAL_ADMINACCOUNTPASSWORD` in your `.env` file.*
8. Open `http://localhost/` in a browser and log in with the admin credentials.
9. **Recommended:** Create a separate account for in-game sign-in. New accounts have no permissions by default; use your admin account to create roles and assign them to users.

To stop the server, run `.\down.ps1` (Windows) or `./down.sh` (Linux/macOS).

## Client Setup

1. Download the [Minecraft Earth Patcher](https://github.com/Earth-Restored/Minecraft_Earth_Patcher/releases) or build it from source.
2. Extract the downloaded archive.
3. Obtain the required game package for your device:
   * **Android:** Acquire a Minecraft Earth `.apk` file (e.g., dumped from your phone).
   * **iOS:** Acquire a Minecraft Earth `.ipa` file.
4. Launch the patcher and select your OS platform.
5. Select your `.apk` or `.ipa` file.
6. Enter the connection details displayed on the Web Portal home page ([http://localhost/](http://localhost/)).
   * *iOS Note:* **Do not enable "Use Custom Auth Server"** as it is not yet fully implemented.
7. Click **Patch**.
8. Transfer the patched `.apk` or `.ipa` file to your mobile device and install it.
    * *iOS Note:* AltStore is recommended.
9. Once that's done, congratulations! You can now open the newly installed Solace app and play Minecraft Earth!
