# Installation

Solace can run with Docker on a desktop/server, or natively on Android using Termux.
The Termux setup does not use Docker: PostgreSQL runs in Termux itself, while the Aspire
AppHost and the Solace services run inside a proot-distro Ubuntu environment.

## Termux (Android)

### Requirements

* **Termux** installed from [F-Droid](https://f-droid.org/packages/com.termux/) (the Play Store build is outdated)
* Android 8 or newer, arm64 device
* About 10 GB of free storage for the Ubuntu rootfs, .NET SDK, NuGet packages and the Solace build
* A stable Wi-Fi connection for the first install (it downloads PostgreSQL, the .NET SDK, Java and NuGet packages)
* The Minecraft Earth resource pack (`vanilla.zip`, see [Build.md](Build.md))

### Install

Run this from Termux:

```bash
curl -fsSL https://raw.githubusercontent.com/Earth-Restored/Solace/refs/heads/aspire/install.sh | bash
```

A prebuilt Termux bundle can skip the first full compile. Trigger the `Termux Aspire
Bundle` workflow, download `termux-aspire.zip`, then run the installer with
`SOLACE_PREBUILT_URL` set to its URL (and optionally `SOLACE_PREBUILT_SHA256`). See
[Build.md](Build.md#termux-prebuilt-bundle) for the workflow and checksum details.

The installer:

1. Installs `proot-distro` and an Ubuntu rootfs.
2. Sets up PostgreSQL in Termux and creates the `solace` role plus the `EarthDb`,
   `PlayfabDb` and `WebPortalDb` databases.
3. Installs the .NET 11 SDK, the Aspire CLI and Java 21 inside Ubuntu.
4. Clones the `aspire` branch to `~/Solace` inside Ubuntu.
5. Writes the runtime configuration to `~/.config/solace/termux.env`.
6. Installs the `earth` launcher and `socat` (used to expose the Aspire dashboard on a
   fixed LAN port).

The first install can take 30-90 minutes on a phone (longer on older devices).

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
4. Wait for the first build to finish. The first start compiles the whole solution and
   starts every service; on a phone this can take 15-20 minutes. The admin panel stays
   unreachable until the web portal has started. Subsequent starts are much faster.
   If `earth status` reports **STOPPED** during the first attempt, start it again from the
   `earth` menu. The build and Aspire DCP caches are warm by then, so the second attempt
   normally gets further and succeeds.
5. Open the admin panel at the URL printed by `earth status` (for example
   <http://192.168.1.50:5000>). On the phone itself <http://127.0.0.1:5000> also works.
   From the `earth` menu choose **Launch Web Portal**, or run `earth portal`.
6. The initial admin account is `admin@solace.com`. Open **Settings > Show Admin Account**
   in the `earth` menu to read the generated password. Change it after the first login.
7. `earth status` also prints the **Aspire dashboard** URL. That dashboard is the
   development resource/telemetry UI (separate from the Solace admin panel). It is
   reachable from other devices at `http://<phone-LAN-IP>:18888`; `earth status` prints
   the current tokenized login link. The dashboard listens on a random loopback port,
   and `earth` forwards the fixed LAN port `18888` to it across restarts. The login token
   still changes on every start, so use the link from `earth status` rather than
   bookmarking the bare address. Anyone on the same network with the current link can
   open the dashboard. From the `earth` menu choose **Launch Aspire Panel**, or run
   `earth aspire`.

The `earth` menu runs in Termux, while the server runs in its own background proot session.
Exiting the menu or closing the terminal window does not stop the server; use
**Start/Stop Server** in the menu (or `earth stop`) to stop it. Keep the Termux app alive
(and exempt it from Android battery optimization) while the server is running.

### Playing from another device

The Termux installer detects the phone's Wi-Fi address and writes it into the advertised
endpoints. The public services bind directly to their ports on all interfaces, so another
device on the same network can connect without a reverse proxy. If the address is wrong,
or the phone gets a new address from the router, update it:

1. Find the Android device's LAN IP address (for example `192.168.1.50`). In Termux,
   run `ifconfig wlan0` and read the `inet` value.
2. In the `earth` menu, open **Settings > Change Public IP** and enter that address.
3. Restart the server (`earth stop` then `earth start`).
4. On the other device, open `http://<that-address>:5000` and use the same address in
   the Minecraft Earth patcher.

`earth status` prints the admin panel URL for the configured address. Both devices must
be on the same subnet; guest Wi-Fi networks often enable **client/AP isolation**, which
blocks device-to-device connections even when both are online. Disable isolation on the
router, or use a regular Wi-Fi network/hotspot. A DHCP reservation for the phone avoids
updating the address later.

The individual services listen on these ports; no nginx reverse proxy is used:

| Service        | Port  |
|----------------|-------|
| Web Portal     | 5000  |
| Locator        | 8080  |
| Auth Server    | 8088  |
| API Server     | 8089  |
| CDN            | 8090  |
| Buildplates    | 19132 and up (TCP and UDP) |
| Aspire dashboard | 18888 (LAN, token required) |

The Aspire dashboard is exposed on port 18888 so it can be opened from another device;
its login token is only valid for the current server session. Android's battery
optimization must not kill Termux while the server is running.

### Updating and uninstalling

* **Update:** run `earth`, open **Update Solace**, and choose the `aspire` branch.
* **Stop:** run `earth` and select **Start/Stop Server**.
* **Uninstall:** run `earth uninstall` from Termux. This removes the Ubuntu-side files,
  the databases (and the PostgreSQL cluster if the installer created it), the
  configuration and the `earth` command. The proot-distro Ubuntu installation and the
  Termux `postgresql` package itself are left in place.

### Troubleshooting

* Read the AppHost output from **Logs > Startup output** in the `earth` menu.
* If the panel is unreachable from another device, test from the phone first:

  ```bash
  curl -I http://<phone-LAN-IP>:5000/
  ```

  A 200/302 response means the service is listening on the LAN; the problem is then
  router AP/client isolation or a firewall between the two devices. `earth status`
  shows the configured address. If the phone itself cannot reach its LAN address, check
  the endpoint in **Settings > Change Public IP** and restart.
* If <http://127.0.0.1:5000> is still unreachable, run `earth status`. `STARTING` means the
  services are still coming up (the first start can take 15-20 minutes on a phone).
  `STOPPED` means the AppHost exited; check the logs and start it again from the menu.
  The Aspire dashboard URL (`earth status`) shows the live resource states and works on
  port 18888 from other devices. If port 18888 refuses connections, install `socat` in
  Termux (`pkg install socat`) and restart the server; the dashboard's own random
  loopback port is not reachable from the LAN.
* PostgreSQL runs in Termux; its log file is
  `$PREFIX/var/lib/postgresql/server.log`.
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
