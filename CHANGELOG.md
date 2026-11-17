# Changelog

## 0.2.0 — Unreleased

### Added

- **Linux support (X11).** One codebase for both systems:
  - Clipboard capture with XFixes, and its own clipboard owner, including large transfers.
  - Global shortcut with `XGrabKey`, and paste-back with XTest. Terminals get Ctrl+Shift+V.
  - Encryption key in the desktop keyring (Secret Service).
  - OCR with Tesseract when it's installed.
  - Tray icon via StatusNotifierItem, autostart via an XDG autostart entry.
  - App names and icons from .desktop files.
  - Honors KDE's `x-kde-passwordManagerHint` (set by KeePassXC and others) and sets it on secrets Clipt pastes.
- **Image storage limit** (default 1 GB, set in Settings → History & privacy). The least recently used images are removed
  first; pinned images and the latest image are kept.
- **Storage in Settings:** disk space used by the history, split by kind of clip.

### Changed

- The UI moved from WPF to Avalonia; icons are now Fluent System Icons. Settings and dialogs use the system title bar.
- Each OS implements one platform interface (`Platform/IPlatform.cs`); everything else is shared.

## 0.1.1 — 2026-11-16

### Changed
- Much lower memory use while Clipt sits in the tray: memory used by the launcher is released a few seconds after it closes.
- Images are decoded in the background, so the list stays responsive. Small encrypted thumbnails are saved
  next to each image, and only the last few full-size previews are kept in memory.
- Opening the launcher and scrolling the list use up to 4× less CPU: code is compiled once, fully optimised,
  instead of being recompiled while you use Clipt.

## 0.1.0 — 2026-11-16

First release.

### Added
- Spotlight-style launcher with Windows 11 acrylic backdrop, accent colour and light/dark themes.
- History of text, rich text, links, code, colours, images and files; instant search with highlighting, `app:` filter,
  type filters, pinning.
- Search inside images using the OCR built into Windows.
- Global shortcut (Alt+Shift+V, with automatic fallback), optional Win+V takeover, paste into the previous app.
- Optional AI actions with Claude (Anthropic API) or a local Ollama model.
- Encryption at rest (AES-256-GCM, DPAPI-wrapped key, keyed fingerprints, no on-disk search index, secure delete).
- Detection of passwords, API keys, tokens, private keys, card numbers and IBANs. Detected secrets are masked,
  unsearchable, never sent to AI, auto-deleted, kept out of Windows clipboard history, and wiped from the clipboard after pasting.
- Exclude apps by name and icon (app picker or *Never record from…*). Copies from private browser windows and
  password-manager copies marked private are skipped.
- Launcher hidden from screen capture and screen sharing.
- `--background` and `--settings` command-line switches.
- Security test suite (47 tests).

### Fixed
- Mouse click and right-click now select the clicked row.
- Mouse wheel scrolls three rows per notch with easing; the scrollbar has a usable thumb and click-to-page track.
- The whole history is listed instead of the first 400 clips.
