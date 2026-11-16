# Development guide

## Requirements

- Windows 10 2004+ (Windows 11 22H2+ for the acrylic/mica look)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Any editor: Visual Studio, Rider, or VS Code with C# Dev Kit

## Build, run, test

```powershell
dotnet build                       # Debug build
dotnet run                         # run it
dotnet test tests/Clipt.Tests      # security & storage tests
```

### Run an isolated instance

Set `CLIPT_DATA_DIR` to use a separate history and settings folder. In this mode Clipt also never writes the
*Start with Windows* registry entry. This is the safest way to develop without touching your real history.

```powershell
$env:CLIPT_DATA_DIR = "$env:TEMP\clipt-dev"
dotnet run -- --background
```

Clipt is single-instance per user session. Quit any running copy (tray → Quit) before starting another build.

### Publish

```powershell
dotnet publish Clipt.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

This produces `publish\Clipt.exe` (~40 MB, framework-dependent). Users need the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
Use `--self-contained true` for a build that doesn't need the runtime installed. It's considerably larger.

## Project layout

```
Clipt.csproj, app.manifest, App.xaml(.cs)
Models/        ClipItem (display logic, masking), ObservableObject
Native/        Win32 P/Invoke, DWM effects, message-only window
Services/      capture, storage, crypto, detection, paste, hotkeys, OCR, AI, settings, app catalog
UI/            launcher, settings, app picker, tray, converters, smooth scrolling, icon
Themes/        Dark.xaml / Light.xaml brushes, Controls.xaml styles
tests/Clipt.Tests/   xUnit tests (referenced by the main project via InternalsVisibleTo)
docs/          user guide, security, architecture, this file
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
- **Theme resources:** use `DynamicResource` brushes from `Themes/*.xaml` (`Fg.*`, `Bg.*`, `Stroke.*`, `Accent*`) so light/dark
  and accent changes apply live.
- **Pinned dependencies.** Don't use floating versions.

## Common tasks

### Add a secret pattern

1. Add the regex or check in `Services/SensitiveDetector.cs`. Prefer provider-specific prefixes over generic entropy rules.
2. Add positive cases to `SensitiveDetectorTests.Detects_secrets`, and look-alikes that must **not** match to
   `Ignores_ordinary_text`.
3. `dotnet test tests/Clipt.Tests`.

### Add an AI action

Add an `AiAction` to `AiService.ActionsFor` with a name, a Segoe Fluent icon glyph, an instruction, and whether it applies to
text and/or images. The system prompt already asks for paste-ready output.

### Add a setting

1. Add a property with a default in `Services/AppSettings.cs`. If it changes meaning, migrate it in `Normalize()`.
2. Add a row to `UI/SettingsWindow.xaml` and load/save it in `SettingsWindow.xaml.cs` (`Collect()`).
3. Apply it in `App.ApplySettings()` if it affects running services.
4. Document it in [user-guide.md](user-guide.md#settings-reference).

## Release checklist

- [ ] `dotnet test tests/Clipt.Tests` passes
- [ ] Bump `<Version>` in `Clipt.csproj`, update `CHANGELOG.md`
- [ ] Publish and smoke-test the exe with `CLIPT_DATA_DIR` pointing to a fresh folder:
  - copy text, a link, a colour, an image and a fake password
  - search, paste, and check the secret is masked
- [ ] Check `dotnet list package --vulnerable --include-transitive` is clean
- [ ] Tag the release and attach `Clipt.exe`
