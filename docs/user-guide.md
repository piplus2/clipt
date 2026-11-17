# Clipt user guide

- [Getting started](#getting-started)
- [On Linux](#on-linux)
- [The launcher](#the-launcher)
- [Searching](#searching)
- [Pasting](#pasting)
- [Organising your history](#organising-your-history)
- [Passwords and other secrets](#passwords-and-other-secrets)
- [Excluding apps](#excluding-apps)
- [AI actions](#ai-actions)
- [Settings reference](#settings-reference)
- [Keyboard reference](#keyboard-reference)
- [Tray icon and command line](#tray-icon-and-command-line)
- [Where your data lives](#where-your-data-lives)
- [Troubleshooting](#troubleshooting)
- [Uninstalling](#uninstalling)

## Getting started

1. Download `Clipt-<version>-win-x64.exe` (Windows) or `Clipt-<version>-linux-x64` (Linux) from the
   [latest release](https://github.com/piplus2/clipt/releases/latest) and run it (see the [README](../README.md#install) for details, or to build from source).
2. A tray icon appears and the launcher opens. From now on, everything you copy is recorded.
3. Press **Alt+Shift+V** anywhere to open Clipt, type to search, and press **Enter** to paste.

By default Clipt starts with Windows. Turn this off in **Settings → General → Start with Windows**.
This guide describes Windows; [On Linux](#on-linux) lists what's different there.

> **Shortcut already taken?** If another app owns Alt+Shift+V, Clipt automatically uses the first free
> one of Ctrl+Shift+Space, Win+Alt+V or Alt+Shift+Space and tells you which. You can set any shortcut in Settings.
> You can also make **Win+V** open Clipt instead of the built-in Windows clipboard history.

## On Linux

Clipt works the same way on Linux X11 desktops (GNOME, KDE Plasma, Xfce, Cinnamon, MATE…). The differences:

| | Linux |
|---|---|
| Shortcut | **Alt+Shift+V** by default. The logo key is called **Super** (e.g. Super+Alt+V); *Take over Win+V* doesn't exist |
| Start at login | *Settings → General → Start at login* writes `~/.config/autostart/clipt.desktop` |
| Pasting into terminals | Clipt sends **Ctrl+Shift+V** to known terminals (GNOME Terminal, Konsole, kitty, Alacritty, …) and Ctrl+V elsewhere |
| Text in images | Needs Tesseract: `sudo apt install tesseract-ocr` (add e.g. `tesseract-ocr-deu` for more languages). Uses English plus your own language if installed |
| Password managers | Copies marked with KDE's `x-kde-passwordManagerHint` (KeePassXC and others) are never recorded, and secrets Clipt pastes carry the same mark |
| Hiding from screen sharing | Not available: X11 has no way to do it |
| Encryption key | In your desktop keyring (GNOME Keyring, KWallet, KeePassXC with Secret Service), unlocked at login. With no keyring, a file only your account can read (Settings says which) |
| Data folder | `~/.local/share/clipt` (or `$XDG_DATA_HOME/clipt`) |
| Tray icon | Standard StatusNotifierItem. On GNOME it needs the AppIndicator extension (built into Ubuntu) |

**Wayland.** Wayland doesn't allow apps to watch the clipboard, register global shortcuts or type into other windows.
Under XWayland, the shortcut and paste-back reach X11 apps only. For the full experience choose an X11 session at
login ("GNOME on Xorg", "Plasma (X11)").

**Uninstalling on Linux:** quit Clipt from the tray, turn off *Start at login* (or delete
`~/.config/autostart/clipt.desktop`), delete the executable and `~/.local/share/clipt`, and remove
"Clipt clipboard history key" from your keyring (e.g. with *Passwords and Keys*).

## The launcher

![The Clipt launcher: search box and filters on top, your history on the left, a preview of the selected clip on the right, and the available shortcuts in the footer](images/launcher.png)

- It opens centred on the monitor you're working on and closes as soon as it loses focus.
- Typing always goes to the search box, even after you click a row.
- The footer shows the shortcuts available right now.
- Clips are recognised by type: **text**, **code** (shown in a monospace font), **links**, **colours** (with a
  swatch and HEX/RGB/HSL values), **images** (with any text found in them) and **files**.

| A colour | A link |
|---|---|
| ![Colour preview with a large swatch and its HEX, RGB and HSL values](images/color.png) | ![Link preview in the light theme, with the site name and an Open in browser shortcut](images/launcher-light.png) |

Clipt follows your Windows light/dark mode and accent colour, or you can pick a theme in **Settings → General → Appearance**.

## Searching

| Type | Finds |
|---|---|
| `invoice march` | clips containing both words, in any order |
| `cafe` | also `Café` (search ignores case and accents) |
| `app:chrome` | clips copied from apps whose name contains "chrome" |
| `app:word budget` | "budget" copied from Word |

- **Text inside images is searchable.** Clipt reads screenshots and pictures with the OCR built into Windows, on your PC.
  Here, searching for `revenue` finds a screenshot of a chart, and the preview shows the text that was found in it:

  ![Searching for "revenue" finds a chart screenshot; the preview lists the recognised text under "Text in image"](images/search-ocr.png)
- **Filters:** click a chip or press **Ctrl+1 … Ctrl+7** (All, Text, Images, Links, Files, Colors, Pinned).
  **Ctrl+Tab** / **Ctrl+Shift+Tab** cycle through them.
- **Secrets aren't searchable by their content** (see below). You can still find them by their label, e.g. `password`.

## Pasting

| Keys | What happens |
|---|---|
| **Enter** (or double-click) | Clipt closes, returns to the app you were in and pastes, with formatting when available |
| **Shift+Enter** | Paste as plain text |
| **Ctrl+Enter** / **Ctrl+C** | Copy to the clipboard only (paste it yourself) |
| **Alt+1 … Alt+9** | Paste the 1st…9th clip in the list |

If you prefer Enter to only copy, turn off **Settings → General → Paste directly**.

Pasting a clip moves it back to the top of the history.

## Organising your history

| Keys | Action |
|---|---|
| **Ctrl+P** | Pin / unpin. Pinned clips are never deleted automatically and appear under *Pinned*. |
| **Shift+Delete** | Delete the selected clip |
| **Ctrl+O** | Open a link in your browser, show a file in Explorer, or save an image as a PNG |
| Right-click | All of the above, plus *Never record from this app* |

By default, clips older than **30 days** are removed and the history keeps up to **2,000** clips. Pinned clips
are exempt from both limits. Change these limits in **Settings → History & privacy**, or use **Clear history…** (it keeps pinned clips).

## Passwords and other secrets

Clipt recognises these when you copy them:

- passwords and random-looking strings
- API keys and tokens (Anthropic, OpenAI, AWS, GitHub, GitLab, Slack, Stripe, Google, Azure, npm, SendGrid, JWTs)
- private keys (SSH, PGP, PEM)
- payment card numbers (checksum-verified)
- bank account numbers (IBAN, checksum-verified)
- passwords inside links (`https://user:pass@host`) or configuration (`Password=…`, `API_KEY=…`)

**What happens to a detected secret** (the default, *Keep briefly, hidden*):

- It's shown as `••••••••` with the reason ("Looks like a password").
  - **Ctrl+R** shows or hides it.
  - Everything is hidden again when Clipt closes or you lock your PC.
- **Enter** pastes it without showing it.
- It's never searchable by content and never sent to AI.
- When pasted, it's marked private so it stays out of Windows clipboard history (Win+V) and cloud clipboard sync,
  and it's **wiped from the clipboard after 30 seconds**, unless you've copied something else since.
- It's deleted automatically **10 minutes** after its last use. **Ctrl+P** keeps it.

![A detected GitHub token shown masked, labelled "API key or token", with a note that it auto-deletes in 7 minutes](images/secret.png)

Choose **Never save** in **Settings → Privacy & security** to have secrets ignored completely.

**Ctrl+H** marks any clip as sensitive by hand, or removes the mark if detection got it wrong.

Clipt also never records:

- copies that password managers mark as private (KeePass, 1Password, Bitwarden and others do this)
- copies made in private browsing windows of Edge, Firefox, Brave and Opera. Chrome's Incognito windows can't be detected, so add Chrome to the excluded apps if you need this.

## Excluding apps

There are two ways to stop Clipt recording copies from an app:

- **From a clip:** right-click a clip that came from the app and choose **Never record from *App***.
- **From Settings:** go to **Settings → Privacy & security → Never record from these apps → Add app**, then pick the app by name.
  The list shows apps you've copied from first, then apps that are running, then installed apps.
  **Browse…** lets you choose a program file directly.

Common password managers are excluded out of the box. Remove an app by clicking **×** on its chip.

Excluding an app doesn't delete clips already recorded from it; delete those with Shift+Delete.

## AI actions

AI is **off** until you enable it in **Settings → AI**. You can use:

- **Claude** (Anthropic API): paste an API key from [console.anthropic.com](https://console.anthropic.com/settings/keys).
  The key is stored encrypted for your Windows account. The `ANTHROPIC_API_KEY` environment variable also works.
- **Ollama**, a model running on your own PC. Nothing leaves your machine. Use a vision model such as `llava` or
  `gemma3` for image actions. A remote Ollama server must use `https://`.

**Using it:**

1. Select a clip and press **Tab**.
2. Pick an action, or type a question ("translate to German and make it formal", "what does this regex match?") and press **Enter**.
3. The answer appears as it's written. **Enter** pastes it, **Ctrl+Enter** copies it, **↑/↓** picks another action, and **Esc** goes back.

**Available actions:**
- **Text:** Summarize, Improve writing, Fix spelling & grammar, Make shorter, Make it professional, Make it friendly,
  Bullet points, Translate to *(your language)*, Explain, Reply to this.
- **Images:** Describe image, Extract text, Image to alt text.

AI results are added to your history (source: *Clipt AI*).

A clip is sent only when you run an action on it, and **secrets are never sent**.

## Settings reference

Open with **Ctrl+,** in the launcher, the ⚙ button, the tray menu, or `Clipt.exe --settings`.

<img src="images/settings.png" width="560" alt="The Settings window, General section: shortcut, Win+V takeover, paste directly, start with Windows and appearance">

| Setting | Default | Notes |
|---|---|---|
| Shortcut | Alt+Shift+V | Click the box and press a new combination |
| Take over Win+V | Off | Replaces the Windows clipboard history shortcut |
| Paste directly | On | Off: Enter only copies |
| Start with Windows | On | Per-user, no admin rights needed |
| Appearance | System | System / Dark / Light; follows your accent colour |
| Keep clips for | 30 days | 0 = forever |
| Maximum clips | 2,000 | Oldest unpinned clips are removed first |
| Capture images | On | |
| Search text inside images | On | Windows OCR, on your PC |
| Protect passwords and secrets | On | |
| When a secret is copied | Keep briefly, hidden | or *Never save* |
| Delete secrets after | 10 minutes | Since last use; pinned secrets are kept |
| Skip private browsing windows | On | |
| Hide Clipt when sharing your screen | On | Invisible to Teams/Zoom/Meet, recordings and screenshots |
| Never record from these apps | Password managers | |
| AI actions | Off | Claude (default model `claude-opus-5-5`) or Ollama |
| Translate to | English | Target of the Translate action |

## Keyboard reference

**Anywhere in Windows:** **Alt+Shift+V** (configurable) opens or closes Clipt. Optionally **Win+V** does too.

**In the launcher:**

| Keys | Action |
|---|---|
| type | Search |
| ↑ ↓ · PgUp PgDn · Ctrl+Home/End | Move the selection |
| Enter · Shift+Enter · Ctrl+Enter | Paste · paste plain text · copy only |
| Ctrl+C | Copy the selected clip (when no search text is selected) |
| Alt+1…9 | Paste the Nth clip |
| Ctrl+1…7 · Ctrl+Tab | Filters |
| Ctrl+P | Pin / unpin |
| Ctrl+H | Mark / unmark as sensitive |
| Ctrl+R | Show / hide a sensitive clip |
| Shift+Delete | Delete clip |
| Ctrl+O | Open link · show file · save image |
| Tab | AI actions |
| Ctrl+, | Settings |
| Esc | Back · clear search · close |

The mouse works too: click to select, double-click to paste, right-click for more, and the wheel to scroll.

## Tray icon and command line

- **Tray icon:** left-click opens Clipt. Right-click gives *Open*, *Pause recording*, *Settings…* and *Quit*.

**Command line:**

| Command | Effect |
|---|---|
| `Clipt.exe` | Start (or show the running instance) |
| `Clipt.exe --background` | Start without opening the launcher (used at sign-in) |
| `Clipt.exe --settings` | Open Settings (in the running instance if there is one) |

Only one instance runs at a time.

## Where your data lives

Everything is in `%LOCALAPPDATA%\Clipt`:

| File | Contents |
|---|---|
| `clips.db` (+ `-wal`, `-shm`) | Your history, **encrypted** |
| `images\*.clip`, `images\*.thumb` | Copied images and their list thumbnails, **encrypted** |
| `vault.key` | The encryption key, locked to your Windows account |
| `settings.json` | Preferences (the API key inside is encrypted) |
| `clipt.log` | Error log, if anything went wrong |

Copying this folder to another PC or user account won't make the history readable there. That's by design.
See [Security & privacy](security.md).

## Troubleshooting

**The shortcut does nothing.**
- Another app probably owns it. Clipt shows a notification when that happens. Pick another shortcut in Settings.
- Elevated (administrator) windows can't receive pastes from normal apps. Run Clipt elevated too if you need that.

**Paste goes to the wrong place or nothing is pasted.**
- Some apps (games, remote desktops) ignore simulated Ctrl+V. Use **Ctrl+Enter** to copy, then paste manually.

**Something I copied isn't in the history.** It may come from:
- an excluded app
- a private browser window
- a password manager that marks copies as private
- a secret, with *Never save* chosen

Recording may also be paused (tray menu → Pause recording).

**Clipt doesn't show up in my screen share or screenshot.** That's the *Hide Clipt when sharing your screen*
setting, which is on by default.

**My history is gone after resetting my Windows password.**
- If an administrator resets the password, Windows can no longer unlock the encryption key.
- Clipt then moves the unreadable history to `%LOCALAPPDATA%\Clipt\unreadable-<date>` and starts fresh.
- Changing your own password normally keeps everything.

## Uninstalling

1. Quit Clipt from the tray icon.
2. Turn off *Start with Windows* first, or delete the `Clipt` value under
   `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`.
3. Delete the Clipt `.exe` and the `%LOCALAPPDATA%\Clipt` folder.
