# Development guide

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- To run the Windows build: Windows 10 2004+ (Windows 11 22H2+ for the acrylic/mica look)
- To run the Linux build: an X11 session with libX11, libXfixes and libXtst (standard on desktops); optionally libXRes,
  libsecret and `tesseract`
- Both targets build on either OS (`EnableWindowsTargeting`), so a Linux CI also catches Windows compile errors.
- Any editor: Visual Studio, Rider, or VS Code with C# Dev Kit

## Build, run, test

```sh
dotnet build                                   # both targets
dotnet run -f net10.0                          # run on Linux
dotnet run -f net10.0-windows10.0.22621.0      # run on Windows
dotnet test tests/Clipt.Tests                  # any OS, against the net10.0 build
```

### Run an isolated instance

Set `CLIPT_DATA_DIR` to use a separate history and settings folder. In this mode Clipt also never writes the
autostart entry. This is the safest way to develop without touching your real history.

```sh
CLIPT_DATA_DIR=/tmp/clipt-dev dotnet run -f net10.0 -- --background     # Linux
```

```powershell
$env:CLIPT_DATA_DIR = "$env:TEMP\clipt-dev"                              # Windows
dotnet run -f net10.0-windows10.0.22621.0 -- --background
```

Clipt is single-instance per user. Quit any running copy (tray → Quit) before starting another build.
On Linux the vault key is still wrapped by your real keyring (entry "Clipt clipboard history key"), even in this mode.

### Publish

```powershell
# Windows: publish\Clipt.exe, framework-dependent
dotnet publish Clipt.csproj -c Release -f net10.0-windows10.0.22621.0 -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

```sh
# Linux: publish/Clipt, a single self-contained executable (~115 MB)
dotnet publish Clipt.csproj -c Release -f net10.0 -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The framework-dependent Windows build needs the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
Use `--self-contained true` for a build that doesn't need the runtime installed. It's considerably larger.

## Building on Linux

These steps build Clipt from source on Linux and install it for your user. They were checked on Ubuntu 24.04;
other distributions only differ in package names.

### 1. Install the system libraries

Clipt needs X11 (with the XFixes and XTest extensions), fontconfig and ICU. These are present on almost every
desktop install. libXRes (accurate source-app names) and libsecret (keyring) are optional but recommended.

```sh
# Debian / Ubuntu
sudo apt install libx11-6 libxfixes3 libxtst6 libxres1 libsecret-1-0 libfontconfig1 libicu-dev
# Fedora
sudo dnf install libX11 libXfixes libXtst libXres libsecret fontconfig libicu
# Arch
sudo pacman -S libx11 libxfixes libxtst libxres libsecret fontconfig icu
```

Optional, for searching text inside images: `sudo apt install tesseract-ocr` (Fedora: `tesseract`, Arch:
`tesseract tesseract-data-eng`).

### 2. Install the .NET 10 SDK

Use your distribution's `dotnet-sdk-10.0` package if it has one, or Microsoft's install script, which needs no
root and installs into `~/.dotnet`:

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"   # add this line to ~/.bashrc too
dotnet --version                                                # should print 10.0.x
```

### 3. Get the source

```sh
git clone https://github.com/piplus2/clipt.git
cd clipt
git checkout linux-port      # Linux support isn't on main yet
```

### 4. Build, test and run

`-f net10.0` selects the Linux build. Without it `dotnet` also builds the Windows target, which works but takes
longer and downloads the Windows SDK packs.

```sh
dotnet build Clipt.csproj -f net10.0
dotnet test tests/Clipt.Tests
dotnet run -f net10.0
```

To try it without touching your real history (and without creating an autostart entry), see
[Run an isolated instance](#run-an-isolated-instance).

### 5. Publish a single executable

```sh
dotnet publish Clipt.csproj -c Release -f net10.0 -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

This produces `publish/Clipt`, a single self-contained file (~115 MB) that runs without .NET installed.
Use `-r linux-arm64` for 64-bit ARM.

### 6. Install it for your user

```sh
install -Dm755 publish/Clipt ~/.local/bin/clipt
mkdir -p ~/.local/share/applications
cat > ~/.local/share/applications/clipt.desktop <<'EOF'
[Desktop Entry]
Type=Application
Name=Clipt
Comment=Clipboard history
Exec=clipt
Terminal=false
Categories=Utility;
EOF
clipt &
```

Clipt now appears in your app menu. On first run it turns on *Start at login* (Settings → General). Press
**Alt+Shift+V** to open it. To update, publish again and repeat the `install` line after quitting Clipt from the tray.

To uninstall, see [On Linux](user-guide.md#on-linux) in the user guide.

## Project layout

```
Clipt.csproj, app.manifest (Windows), Program.cs, App.axaml(.cs)
Models/              ClipItem (display logic, masking), ObservableObject
Platform/            IPlatform (the OS boundary) and PlatformDefaults
Platform/Windows/    Win32, clipboard, hotkey, Win+V hook, tray, OCR, DPAPI        (Windows build only)
Platform/Linux/      X11 session (clipboard, hotkey, paste), keyring, Tesseract, tray, .desktop entries   (Linux build only)
Services/            capture, storage, crypto, detection, paste, hotkeys, images, AI, settings, app catalog
UI/                  launcher, settings, app picker, dialogs, highlighter, icon
Themes/              Controls.axaml styles (theme brushes live in App.axaml)
tests/Clipt.Tests/   xUnit tests (referenced by the main project via InternalsVisibleTo)
docs/                user guide, security, architecture, this file
```

See [architecture.md](architecture.md) for how the pieces fit together.

## Conventions

- **Security first.**
  - No clip content may be written to disk unencrypted, including logs, temp files and caches.
  - New persisted fields that can contain user content must go through `CryptoVault`, and get a test in
    `StorageSecurityTests.Nothing_copied_is_written_to_disk_in_plaintext`.
- **Sensitive clips** must stay masked, unsearchable by content, out of AI and out of rich formats. If you add a new
  surface that shows or exports clip content, check `ClipItem.IsMasked` / `Sensitive`.
- **Typing goes to the search box.** Controls in the launcher are non-focusable on purpose. Handle mouse input explicitly.
- **Theme resources:** use `DynamicResource` brushes from `App.axaml` (`Fg.*`, `Bg.*`, `Stroke.*`, `Accent*`) so light/dark
  and accent changes apply live.
- **OS-specific code goes behind `IPlatform`.** Shared code must not call Win32, Xlib or OS tools directly. When you add
  a member, implement it for both OSes and build both targets.
- **Pinned dependencies.** Don't use floating versions.

## Common tasks

### Add a secret pattern

1. Add the regex or check in `Services/SensitiveDetector.cs`. Prefer provider-specific prefixes over generic entropy rules.
2. Add positive cases to `SensitiveDetectorTests.Detects_secrets`, and look-alikes that must **not** match to
   `Ignores_ordinary_text`.
3. `dotnet test tests/Clipt.Tests`.

### Add an AI action

Add an `AiAction` to `AiService.ActionsFor` with a name, a Fluent System Icons name (`FluentIcons.Common.Icon`), an instruction, and whether it applies to
text and/or images. The system prompt already asks for paste-ready output.

### Add a setting

1. Add a property with a default in `Services/AppSettings.cs`. If it changes meaning, migrate it in `Normalize()`.
2. Add a row to `UI/SettingsWindow.axaml` and load/save it in `SettingsWindow.axaml.cs` (`Collect()`).
3. Apply it in `App.ApplySettings()` if it affects running services.
4. Document it in [user-guide.md](user-guide.md#settings-reference).

### Update the screenshots

The screenshots in `docs/images/` come from an isolated instance filled with made-up clips, never from a real history.

1. Start an isolated instance (see [above](#run-an-isolated-instance)) with this `settings.json` in `CLIPT_DATA_DIR`:
   ```json
   { "FirstRunDone": true, "HideFromScreenCapture": false, "Theme": "Dark" }
   ```
   `HideFromScreenCapture` must be off, or the launcher is invisible to screenshot tools.
2. Copy some demo clips. Secrets auto-delete after 10 minutes, so copy those last.
3. Cover the screen with a borderless full-screen window showing an abstract wallpaper (soft colour blobs on a
   dark background for dark-theme shots, pastel on light for light-theme ones). It keeps your real desktop out of the
   shots and gives the acrylic something to pick up.
4. Capture at 100% display scaling, including about 230 px of wallpaper left and right of the launcher and 150 px
   above and below it, so the window's shadow shows, and save as PNG under the existing name. The launcher closes
   when it loses focus, so use a capture that doesn't take focus, e.g. `Graphics.CopyFromScreen` from a script.

## Release checklist

- [ ] `dotnet test tests/Clipt.Tests` passes
- [ ] Bump `<Version>` in `Clipt.csproj`, update `CHANGELOG.md`
- [ ] `dotnet build` (both targets)
- [ ] Publish and smoke-test on Windows and on Linux (X11) with `CLIPT_DATA_DIR` pointing to a fresh folder:
  - copy text, a link, a colour, an image and a fake password
  - search, paste, and check the secret is masked
- [ ] Check `dotnet list package --vulnerable --include-transitive` is clean
- [ ] Tag the release and attach `Clipt-<version>-win-x64.exe` and `Clipt-<version>-linux-x64`
