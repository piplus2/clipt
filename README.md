# Clipt

**A Spotlight-style clipboard history for Windows.** Press a shortcut, type a few letters, hit Enter, and it's pasted.

- **Everything you copy:** text, rich text, links, code, colours, images and files, with a live preview.
- **Instant search:** search ignores case and accents, matches are highlighted, and filters let you narrow by type
  or by app (`app:chrome`). **Text inside screenshots is searchable** too, thanks to the OCR built into Windows.
- **Feels native:** Windows 11 acrylic, your accent colour, light/dark mode, multi-monitor and per-monitor DPI,
  tray icon, starts with Windows, and can even take over **Win+V**.
- **Pastes where you were:** Enter closes Clipt and pastes into the app you were using. Shift+Enter pastes plain text.
- **Secure by design:** your history is encrypted, and passwords and keys are recognised and hidden automatically. [Details below](#security--privacy).
- **Optional AI:** press Tab on a clip to summarize, rewrite, translate, explain or ask anything, with Claude or a
  local model via Ollama. Off until you turn it on.

## Install

Clipt needs Windows 10 2004+ (Windows 11 22H2+ for the full look) and the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

Build it from source:

```powershell
git clone https://github.com/piplus2/clipt.git
cd clipt
dotnet publish Clipt.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
.\publish\Clipt.exe
```

Then press **Alt+Shift+V** anywhere.

## Everyday use

| Keys | Action |
|---|---|
| **Alt+Shift+V** | Open / close Clipt (configurable; optionally Win+V) |
| type | Search |
| **Enter** · **Shift+Enter** · **Ctrl+Enter** | Paste · paste plain text · copy only |
| **Alt+1…9** | Paste the Nth clip |
| **Ctrl+P** | Pin (pinned clips are kept forever) |
| **Ctrl+H** · **Ctrl+R** | Mark as sensitive · show a hidden secret |
| **Shift+Del** | Delete |
| **Tab** | AI actions |
| **Ctrl+,** | Settings |

The full list and every setting are in the **[user guide](docs/user-guide.md)**.

## Security & privacy

A clipboard manager sees your passwords, so Clipt treats every clip as potentially sensitive:

- **Encrypted at rest:**
  - Everything copied is encrypted with AES-256-GCM, including images, text found in images and the source app.
  - The key is locked to your Windows account with DPAPI.
  - There's no plaintext search index, and deleted clips are overwritten.
- **Secrets detected automatically:** passwords, API keys and tokens, private keys, card numbers and IBANs are:
  - masked and kept out of search and AI
  - deleted after 10 minutes
  - kept out of Windows clipboard history when pasted, and wiped from the clipboard 30 s later
- **Not recorded:**
  - password-manager copies marked private
  - apps you exclude, picked by name and icon
  - private browsing windows
- **Invisible to screen sharing:** Clipt doesn't show up in screen shares, recordings or screenshots.

Read the full **[security design and threat model](docs/security.md)**. Report vulnerabilities privately as described in
[SECURITY.md](SECURITY.md).

## Documentation

| | |
|---|---|
| [User guide](docs/user-guide.md) | Using Clipt, settings, shortcuts, troubleshooting |
| [Security & privacy](docs/security.md) | Threat model, cryptography, secret handling, tests |
| [Architecture](docs/architecture.md) | Components, capture pipeline, storage, threading |
| [Development](docs/development.md) | Building, testing, conventions, release checklist |
| [Changelog](CHANGELOG.md) | What's new |

## Development

```powershell
dotnet build
dotnet test tests/Clipt.Tests
$env:CLIPT_DATA_DIR = "$env:TEMP\clipt-dev"; dotnet run   # isolated history; no autostart entry
```

See the [development guide](docs/development.md).
