# Going Cooperative

Experimental two-player cooperative multiplayer for **Going Medieval**.

Going Cooperative uses a host-authoritative model: the host runs the game
simulation, the client sends player intent, and the host returns authoritative
world and presentation state.

> [!WARNING]
> Going Cooperative is pre-release software. Gameplay coverage is incomplete,
> protocol details may change, and desynchronization or save problems remain
> possible. Back up every save before using the mod.

## Requirements

Each player needs:

- The same Going Medieval version on Windows x64.
- The same Going Cooperative release.
- A legitimate local Going Medieval installation.
- A reachable LAN/VPN connection to the host, or the experimental Steam relay
  mode.

The player release already includes the official **BepInEx 5.4.23.5 Windows
x64** loader. Players do not install BepInEx separately.

## Easy installation

1. Close Going Medieval.
2. Back up important saves.
3. Open the [Going Cooperative Releases
   page](https://github.com/reality-comes/Going-Cooperative/releases).
4. Download `Going-Cooperative-vX.Y.Z-win-x64.zip` from the release's
   **Assets** section.
5. In Steam, right-click Going Medieval and select **Manage > Browse local
   files**.
6. Extract the **contents** of the ZIP directly into that directory. Allow
   Windows to merge folders and replace older Going Cooperative/BepInEx files.
7. Launch Going Medieval.

Do not download GitHub's automatically generated source-code archives unless
you intend to compile the mod.

The correct installation layout is:

```text
Going Medieval/
|-- .doorstop_version
|-- doorstop_config.ini
|-- winhttp.dll
|-- BepInEx/
|   |-- core/
|   `-- plugins/
|       `-- GoingCooperative/
|           `-- GoingCooperative.dll
|-- GoingCooperative/
|   `-- replication.cfg
|-- Licenses/
|-- Going Medieval.exe
`-- Going Medieval_Data/
```

Do not leave the files inside an extra wrapper directory such as:

```text
Going Medieval/Going-Cooperative-v0.3.0-win-x64/BepInEx/...
```

### Existing BepInEx installations

The bundle contains the unmodified official BepInEx 5.4.23.5 Windows x64
distribution. If BepInEx is already installed, extraction may merge with it.
Back up custom plugins and configuration first. Remove incompatible BepInEx 6,
x86, Unix, or modified loader files before installing this Windows x64 bundle.

The release does not contain Going Medieval or Unity game files.

## Configuration

The release installs:

```text
Going Medieval/GoingCooperative/replication.cfg
```

Use the same release configuration on both computers. Ordinary players do not
need to edit or rename it. The Multiplayer menu chooses host/client role,
address, port, save, and connection mode in memory.

The packaged configuration:

- enables the currently supported replication systems;
- enables authenticated Direct/IP networking;
- disables optional diagnostics, probes, snapshot logging, and verbose
  replication logging.

Changing functional replication gates can make the peers incompatible. If an
advanced user edits `replication.cfg`, make the same change on both machines and
restart both games.

## Starting a session

Use the remapped **Multiplayer** button on the main menu. Going Cooperative is
currently designed for exactly two players.

### Direct/IP mode

Direct mode uses authenticated networking. The host receives a temporary
session code in the Multiplayer menu. Share the host address, port, and session
code privately with the client. The client must enter the same code.

The session code protects the Direct connection with endpoint pinning,
authenticated UDP packets, replay rejection, and authenticated save/control
transfer. A wrong or missing code is rejected.

For the default port `47692`, the host must permit Going Medieval through
Windows Firewall and allow inbound **UDP and TCP** traffic. Internet play
normally requires a private VPN or correctly configured port forwarding.

#### Host

1. Select **Multiplayer**.
2. Choose **Host** and Direct/IP mode.
3. Select the settlement save.
4. Confirm the port.
5. Give the client the reachable LAN/VPN address and generated session code.
6. Wait for the client and save transfer to reach **Connected**.
7. Select **Play** after the client is ready.

#### Client

1. Select **Multiplayer**.
2. Choose **Join** and Direct/IP mode.
3. Enter the host's LAN/VPN address, port, and session code.
4. Select **Connect**.
5. Wait for the host save to transfer and verify.
6. Wait for **Connected**, then select **Play**.

Do not manually load a different save on the client after connecting.

### Steam mode (experimental)

When both players own the Steam version, the Multiplayer menu can use a
friends-only Steam lobby and overlay invite. Steam relay mode normally avoids
manual VPN and port-forwarding setup. Both players must still use the same
Going Cooperative and Going Medieval versions.

## Full Session Resync

During multiplayer, a compact HUD appears at the top center. The client can use
**FULL RESYNC** when important world state no longer matches the host.

1. The client selects **FULL RESYNC**.
2. Both games enter the resync overlay.
3. The host creates a fresh authoritative checkpoint.
4. The checkpoint transfers to and verifies on the client.
5. Both games pass through the mod's blank transition and reload the checkpoint.
6. Replication resumes after the synchronized load.

Do not close either game or load another save during resync. Press `F8` if the
Multiplayer panel must be reopened.

## Verifying the installation

After reaching the main menu, these paths should exist:

```text
BepInEx/LogOutput.log
BepInEx/GoingCooperative/plugin.log
BepInEx/plugins/GoingCooperative/GoingCooperative.dll
GoingCooperative/replication.cfg
```

`BepInEx/LogOutput.log` should contain:

```text
Going Cooperative host-authoritative replication plugin loaded.
```

The Multiplayer button should replace the normal Tutorial button.

## Updating

Close Going Medieval, download the newer Windows x64 release ZIP, and extract
its contents into the game directory. Allow it to replace the older bundled
files. Do not mix plugin DLLs or `replication.cfg` files from different
releases.

## Uninstalling

Always close the game first.

Remove:

```text
BepInEx/plugins/GoingCooperative/
GoingCooperative/
```

If no other mod uses BepInEx, you may also remove:

```text
BepInEx/
.doorstop_version
changelog.txt
doorstop_config.ini
winhttp.dll
```

Do not remove the shared BepInEx files if other installed mods need them.

## Troubleshooting

### The Multiplayer button does not appear

- Confirm the files are not inside an extra wrapper directory.
- Confirm the DLL path is exactly
  `BepInEx/plugins/GoingCooperative/GoingCooperative.dll`.
- Confirm `winhttp.dll` and `doorstop_config.ini` are beside
  `Going Medieval.exe`.
- Check `BepInEx/LogOutput.log` for `Going Cooperative`, `error`, or
  `exception`.

### Direct connection fails

- Confirm the host started before the client connected.
- Confirm address and port match.
- Confirm the session code matches exactly.
- Allow Going Medieval and TCP/UDP port `47692` through the host firewall.
- Do not use `127.0.0.1` unless both games run on the same computer.
- Rebooting both games can clear a stale socket after an interrupted test.

### The transferred save will not load

- Confirm both computers use the same Going Medieval version and release ZIP.
- Wait for **Connected** before either player selects **Play**.
- Do not manually load another settlement on the client.
- Check both logs for `save`, `transfer`, `verify`, `hash`, or `load`.

### Full Session Resync does not finish

- Leave both games open while checkpoint creation and transfer complete.
- Confirm the host still has access to its save folder.
- Check both logs for `resync`, `checkpoint`, `transfer`, `verify`, or `load`.
- Restart the multiplayer session if the overlay reports a definite failure.

## Release contents and third-party software

The Windows release bundles the exact official BepInEx 5.4.23.5 x64 archive,
verified before packaging with its published SHA-256. It includes Unity
Doorstop, HarmonyX/Harmony, MonoMod, and Mono.Cecil.

The release contains:

- `THIRD-PARTY-NOTICES.md`;
- complete applicable license texts under `Licenses/`;
- the exact corresponding Unity Doorstop v4.5.0 source archive under
  `Licenses/Source/`;
- `RELEASE-MANIFEST.txt` with component provenance and per-file SHA-256 hashes;
- a separate SHA-256 file for the complete release ZIP.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for exact versions,
licenses, source links, and attribution.

## Building from source

Ordinary players should use the ZIP attached to a GitHub Release.

Developer prerequisites:

- Windows PowerShell 5.1 or newer.
- A .NET SDK containing the Roslyn C# compiler.
- A local Going Medieval Windows x64 installation.
- Internet access for the pinned BepInEx download, or a local copy of the
  official BepInEx archive.

### Compile only

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 `
  -Configuration Release `
  -GameRoot "C:\Path\To\Going Medieval"
```

Output:

```text
artifacts/bin/Release/GoingCooperative.dll
```

### Create the self-contained player ZIP

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Package-Release.ps1 `
  -GameRoot "C:\Path\To\Going Medieval"
```

The script reads the version from the plugin source, builds the DLL, downloads
the pinned official BepInEx Windows x64 archive when necessary, verifies its
SHA-256, downloads and verifies the corresponding Unity Doorstop source,
stages the complete easy-install layout, validates the release config, adds
licenses and a manifest, and creates:

```text
artifacts/Going-Cooperative-v0.3.0-win-x64.zip
artifacts/Going-Cooperative-v0.3.0-win-x64.zip.sha256
```

To build without downloading BepInEx:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Package-Release.ps1 `
  -GameRoot "C:\Path\To\Going Medieval" `
  -BepInExArchive "C:\Downloads\BepInEx_win_x64_5.4.23.5.zip"
```

The supplied archive must match the pinned official SHA-256.
Pass `-UnityDoorstopSourceArchive` in the same way to use a pre-downloaded
official Unity Doorstop v4.5.0 source archive.

## Repository layout

```text
config/                 Release replication configuration
scripts/                Build, package, and validation scripts
src/                    Core and BepInEx plugin source
third-party/licenses/   Redistributed third-party license texts
```

Generated artifacts, game files, active configs, logs, saves, and downloaded
dependencies are not committed.

## License

Going Cooperative is available under the [MIT License](LICENSE). Third-party
components remain under their respective licenses.
