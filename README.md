# Clipt

A Spotlight-style clipboard history for Windows. Press a shortcut, type, hit Enter, and it's pasted.

- **Everything you copy**: text, rich text (HTML/RTF kept for formatted paste), links, code, colours, images and files.
- **Instant search**: full-text search (SQLite FTS5) plus substring matching, with matches highlighted. Use `app:chrome` to filter by source app.
- **Search inside images**: screenshots are OCR'd on your PC with the OCR engine built into Windows, so "invoice" finds a screenshot of an invoice.
- **Native to Windows**: Windows 11 acrylic/mica backdrop, rounded corners, your accent colour, light/dark theme, per-monitor DPI, tray icon, starts with Windows. It can also take over **Win+V**.
- **Pastes where you were**: Enter closes Clipt, puts focus back on your previous app and pastes there.
- **Secure by design**: encrypted history, automatic detection of passwords and secrets, hidden from screen sharing. See [Security & privacy](#security--privacy).
- **Optional AI**: press **Tab** on a clip to summarize, rewrite, fix grammar, translate, explain code, describe an image, or ask anything about it. Choose **Claude** (Anthropic API) or a **local model via Ollama**. Off until you turn it on, and a clip is sent only when you run an action on it.

## Keyboard

| Keys | Action |
|---|---|
| `Alt+Shift+V` (configurable) | Open / close Clipt |
| type | Search |
| `↑` `↓` `PgUp` `PgDn` | Move |
| `Enter` | Paste |
| `Shift+Enter` | Paste as plain text |
| `Ctrl+Enter` / `Ctrl+C` | Copy only |
| `Alt+1…9` | Paste the Nth item |
| `Ctrl+1…7`, `Ctrl+Tab` | Filters: All, Text, Images, Links, Files, Colors, Pinned |
| `Ctrl+P` | Pin / unpin (pinned clips are never pruned) |
| `Ctrl+H` | Mark / unmark as sensitive |
| `Ctrl+R` | Show / hide a sensitive clip's content |
| `Shift+Del` | Delete clip |
| `Ctrl+O` | Open link / reveal file / open image |
| `Tab` | AI actions on the selected clip (type to ask anything) |
| `Ctrl+,` | Settings |
| `Esc` | Back / clear / close |

## Security & privacy

People copy passwords, keys and personal data all the time, so Clipt treats every clip as potentially sensitive.

**Encrypted at rest.** Everything copied (text, rich text, text found in images, the source app, and the images themselves) is encrypted with AES-256-GCM before it touches the disk. The key is random, generated on first run, and wrapped with Windows DPAPI, so only your Windows account on this PC can unlock it. Duplicate detection uses keyed HMAC fingerprints, so the database can't be used to check whether a known password was ever copied. Search runs in memory, so no plaintext search index exists on disk. Deleted clips are overwritten (`secure_delete`), not just unlinked.

**Secrets are recognised automatically.** Passwords, API keys and tokens (Anthropic, OpenAI, AWS, GitHub, GitLab, Slack, Stripe, Google, Azure, JWTs…), private keys, card numbers (Luhn-checked), IBANs (mod-97-checked) and credentials embedded in URLs or connection strings are:
- masked in the list and preview (`Ctrl+R` reveals; everything re-masks when Clipt closes or the PC locks),
- excluded from search by content and never sent to AI (enforced in the AI service, not just hidden in the UI),
- deleted automatically after 10 minutes (configurable; pin to keep),
- marked private when pasted, so they stay out of Windows clipboard history and cloud sync, and wiped from the clipboard 30 seconds later.

Or choose **Never save** to have them ignored entirely. Anything can also be marked sensitive by hand with `Ctrl+H`.

**Never recorded:**
- copies marked private by password managers (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory=0`, `Clipboard Viewer Ignore`)
- copies from apps you exclude. Pick them by name and icon from apps you've copied from, running apps or the Start menu, or right-click a clip and choose *Never record from…*. Popular password managers are excluded out of the box.
- copies from private browsing windows (Edge InPrivate, Firefox, Brave, Opera). Chrome Incognito windows carry no marker in their title, so add Chrome to the excluded apps if needed.

**Other safeguards:**
- The launcher is invisible to screen sharing, recording and screenshots.
- Copied file paths are revealed in Explorer, never executed. Links open only for `http(s)`, `mailto` and `ftp`.
- AI is opt-in and runs per action. Remote Ollama servers must use `https`.
- Your API key is stored with DPAPI.
- If the key can't be unlocked (for example after an admin password reset), the unreadable history is set aside and a new one starts. Data is never "recovered" by weakening encryption.

**Limits worth knowing:** while Clipt is open, its decrypted history is in its own process memory. Malware already running as your user could read it, as it could read the clipboard itself. Encryption at rest protects against other accounts, stolen or imaged disks, backups and synced folders.

Run the security test suite with `dotnet test tests/Clipt.Tests`.

## Build & run

Requires the .NET 10 SDK on Windows 10 19041+ (backdrop effects need Windows 11 22H2+).

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.22621.0\Clipt.exe
```

Single-file publish:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Set `CLIPT_DATA_DIR` to run an isolated instance (separate history/settings, no autostart registration), which is handy for development.

## AI setup

Settings → AI → enable, then either:

- **Claude**: paste an Anthropic API key (stored with Windows DPAPI, or set `ANTHROPIC_API_KEY`). The default model is `claude-opus-5-5`, and you can enter any model ID. Requests use the official `Anthropic` C# SDK with streaming and low effort, for snappy transforms.
- **Ollama**: point to your endpoint (default `http://localhost:11434`) and model. Use a vision model for image actions.

## Layout

```
App.xaml(.cs)          startup, single instance, tray, theme, wiring
Native/                Win32 interop, DWM backdrop, message-only window
Services/              ClipboardMonitor, ClipStore (encrypted SQLite), CryptoVault,
                       SensitiveDetector, PasteService, HotkeyService, WinVHook,
                       OcrService, AiService, AppCatalog, settings
UI/                    LauncherWindow (+ViewModel), SettingsWindow, AppPickerWindow, tray, icon
tests/Clipt.Tests/     detection, encryption, no-plaintext-on-disk, expiry, migration
Themes/                Dark/Light brushes and control styles
```
