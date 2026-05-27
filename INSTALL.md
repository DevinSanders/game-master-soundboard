# Installation guide

Every release on the [Releases page](../../releases) ships native installers for Windows, macOS, and Linux, plus universal archive fallbacks if you'd rather install manually.

Quick chooser:

| You want… | Use |
|---|---|
| One-click install on Windows | `.exe` installer (or `choco install gmsoundboard`) |
| One-click install on macOS | `.dmg` (or `brew install --cask gmsoundboard` once the tap is published) |
| One-click install on Linux Debian/Ubuntu | `.deb` |
| One-click install on Linux Fedora/RHEL/openSUSE | `.rpm` |
| Portable Linux binary, no install | `.AppImage` |
| Manual install on any OS | `.zip` / `.tar.gz` |

---

## Windows

### Option 1 — Installer (recommended)

1. Download `GameMasterSoundBoard-Setup-<version>.exe` from the Releases page.
2. Run it. Click through the wizard.
3. On first launch, Windows SmartScreen may show "Windows protected your PC" because the installer isn't yet signed. Click **More info → Run anyway**. (We'll be enabling [SignPath](https://signpath.org/) free OSS code signing soon, which removes this warning.)

The installer:
- Creates Start Menu and (optionally) desktop shortcuts.
- Registers the `gmsound://` URL protocol so links in Obsidian, browsers, and markdown viewers launch the app.
- Adds an Add/Remove Programs entry so you can uninstall normally.

### Option 2 — Chocolatey

```powershell
choco install gmsoundboard
```

> The Chocolatey package is built by every release but **not** auto-published to the community feed — that step requires human moderation review. If `choco install` doesn't find the package yet, the GitHub Release still has it as a `.nupkg` you can install with `choco install gmsoundboard -s .` after downloading.

### Option 3 — Portable zip

Download `GameMasterSoundBoard-<version>-win-x64.zip`, extract anywhere, and run `SoundBoard.Desktop.exe`. The app will register the `gmsound://` scheme on first launch (writes to `HKCU\Software\Classes\gmsound`).

---

## macOS

### Option 1 — DMG (recommended)

1. Download the right `.dmg` for your Mac:
   - Apple Silicon (M1/M2/M3/M4): `GameMasterSoundBoard-<version>-osx-arm64.dmg`
   - Intel: `GameMasterSoundBoard-<version>-osx-x64.dmg`
2. Open the DMG and drag **Game Master Sound Board** to the Applications folder.
3. Eject the DMG.

**First launch — important.** Because the app isn't notarized through Apple's developer program, Gatekeeper will say *"Game Master Sound Board cannot be opened because the developer cannot be verified."* You have two ways to allow it:

- **Right-click → Open** on the app in Applications (not double-click). The same dialog appears, but now with an **Open** button. Click it; you only need to do this once.
- **Or** from Terminal:
  ```bash
  xattr -cr "/Applications/Game Master Sound Board.app"
  ```

After that, double-click works normally and `gmsound://` links open the app.

### Option 2 — Homebrew

> Not live yet. The Cask formula template lives in [packaging/macos/homebrew/gmsoundboard.rb](packaging/macos/homebrew/gmsoundboard.rb), but the tap repository (`DevinSanders/homebrew-soundboard`) and the per-release version/SHA256 substitution job aren't wired up yet. Until then, use the DMG.

Once published, install will be:

```bash
brew tap DevinSanders/soundboard
brew install --cask gmsoundboard
```

### Option 3 — Manual .app

Download the `*-osx-arm64-app.zip` or `*-osx-x64-app.zip`, extract, and drag the `.app` to Applications yourself. Same Gatekeeper workaround as above.

### Runtime dependencies (macOS)

**None to install.** The `.app` is fully self-contained — the .NET runtime and the OpenAL Soft audio library are bundled inside the bundle, and everything else it needs (CoreAudio for output, AppKit for the window) is part of macOS itself. There is no Homebrew/MacPorts dependency to pull in; the DMG and the `.app` zip run as-is on a clean macOS (Big Sur or newer) once past Gatekeeper.

---

## Linux

Two architectures are published: **x86_64** (`amd64`) for normal PCs and **arm64** (`aarch64`) for Raspberry Pi 4/5 and other ARM boards. Pick the matching artifact. AppImage is x86_64-only — on arm64 use the `.deb`, `.rpm`, or tarball (Raspberry Pi OS is Debian-based, so the `.deb` is the natural choice).

### Option 1 — .deb (Debian, Ubuntu, Mint, Pop!_OS, Raspberry Pi OS, …)

```bash
sudo apt install ./gmsoundboard_<version>_amd64.deb     # PC
sudo apt install ./gmsoundboard_<version>_arm64.deb     # Raspberry Pi / ARM
```

`apt` automatically pulls the handful of system libraries the app needs at runtime (see [Runtime dependencies](#runtime-dependencies-linux) below). The package's post-install hook registers the `gmsound://` URL scheme via `xdg-mime` and refreshes the desktop database, so links open the app immediately.

### Option 2 — .rpm (Fedora, RHEL, openSUSE, Rocky, …)

```bash
sudo dnf install ./gmsoundboard-<version>.x86_64.rpm    # PC
sudo dnf install ./gmsoundboard-<version>.aarch64.rpm   # ARM
```

`dnf` resolves the same runtime dependencies as the `.deb`. Same post-install behavior.

### Option 3 — AppImage (portable, any glibc x86_64 distro)

```bash
chmod +x GameMasterSoundBoard-<version>-x86_64.AppImage
./GameMasterSoundBoard-<version>-x86_64.AppImage
```

AppImage is fully portable — no install, no root, just run. Trade-offs:

- **x86_64 only** — there is no arm64 AppImage. ARM users take the `.deb`/`.rpm`/tarball.
- The `gmsound://` scheme is **not** registered automatically because there's no install step. To register it, move the AppImage to a stable location and install the `.desktop` file inside it manually, or use [`appimaged`](https://github.com/AppImage/appimaged).
- **glibc-only**. The bundled OpenAL Soft native is glibc-linked, so musl distros (Alpine, Void-musl, Chimera) can't load it — you'll see a `dlopen` failure on launch. There, use the tarball plus a system OpenAL Soft (`apk add openal-soft`).
- An AppImage installs no package metadata, so it does **not** auto-pull the runtime libraries below. They're present on any normal desktop; on a stripped-down system install them yourself.

### Option 4 — Portable tarball

Download `GameMasterSoundBoard-<version>-linux-x64.tar.gz` (or `-linux-arm64.tar.gz`), extract anywhere, and run `./SoundBoard.Desktop`. Like the AppImage, the tarball carries no dependency metadata — install the runtime libraries below if the app won't start.

### Runtime dependencies (Linux)

The download is **self-contained**: the .NET runtime and the OpenAL Soft audio library are bundled, so you do **not** install .NET separately. But two sets of *system* libraries are loaded on demand and live outside the package:

- **GUI (Avalonia's X11 backend):** `libX11`, `libICE`, `libSM`, `fontconfig`, and an OpenGL stack (`libGL` — rendering falls back to software if absent).
- **Audio (OpenAL Soft backends):** `libasound` (ALSA) at minimum; `libpulse` / PipeWire are used when present.

The **`.deb` and `.rpm` declare these as package dependencies**, so `apt`/`dnf` install them automatically — nothing extra to do. The **AppImage and tarball don't** (they have no package manager to enforce through); they're present on every mainstream desktop, but on a minimal/headless box install them with e.g. `sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libgl1 libasound2`.

### Linux audio prerequisites

Audio output goes through OpenAL Soft, which picks a backend at runtime: **PipeWire → PulseAudio → ALSA → JACK**. Every mainstream desktop ships at least one. If you launch and get silence with no error:

- Force ALSA: `ALSOFT_DRIVERS=alsa ./SoundBoard.Desktop` (or set the env var globally) — the escape hatch if a distro update breaks libpulse for OpenAL.
- Headless installs may have no audio stack at all — `sudo apt install pulseaudio` (or pipewire / alsa-utils) gives you a working backend.

---

## Discord output (optional plugin)

Discord output is no longer baked into the host — it lives in a separate plugin so the ~25 MB of Discord native libraries only ship if you actually want voice-channel streaming. To install:

1. Grab the latest release zip from the [gmsb-bridge-discord releases page](https://github.com/DevinSanders/gmsb-bridge-discord/releases).
2. In Game Master Sound Board, open **Settings → Plugin Manager** and drag the zip onto the drop zone.
3. Configure the bot token + voice channel in **Settings → Bridges**.

The plugin bundles `libopus`, `libsodium`, and `libdave` per-RID natively, sets up DAVE end-to-end voice encryption automatically (required by Discord from March 2026), and borrows Opus encoding from the `gmsb-codec-opus` plugin (install that one too if you want best-quality outbound audio).

---

## Verifying releases (optional but recommended)

Every artifact's SHA256 sum is shown on the GitHub Release page. To verify:

**Windows (PowerShell):**
```powershell
Get-FileHash GameMasterSoundBoard-Setup-1.2.3.exe -Algorithm SHA256
```

**macOS / Linux:**
```bash
shasum -a 256 GameMasterSoundBoard-1.2.3-linux-x64.tar.gz
```

Compare the output against the value on the Release page.

---

## Uninstalling

| Platform | How |
|---|---|
| Windows installer | Settings → Apps → Game Master Sound Board → Uninstall |
| Chocolatey | `choco uninstall gmsoundboard` |
| Windows portable | Delete the folder. To unregister `gmsound://`, also delete `HKCU\Software\Classes\gmsound` via `regedit`. |
| macOS DMG/Homebrew | Move `Game Master Sound Board.app` to the Trash. To wipe your library too, also delete `~/Library/Application Support/GameMasterSoundBoard/`. The Homebrew cask's `zap` does this automatically: `brew uninstall --cask --zap gmsoundboard`. |
| Linux .deb | `sudo apt remove gmsoundboard` |
| Linux .rpm | `sudo dnf remove gmsoundboard` |
| Linux AppImage | Delete the file. |

User data — your library, settings, plugins — lives outside the install location and is preserved across upgrades and uninstalls. Find it at:

- Windows: `%LocalAppData%\GameMasterSoundBoard\`
- macOS: `~/Library/Application Support/GameMasterSoundBoard/`
- Linux: `~/.local/share/GameMasterSoundBoard/`

---

## Build from source

See [CONTRIBUTING.md](CONTRIBUTING.md#quick-start).
