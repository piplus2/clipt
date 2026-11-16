# Changelog

## Unreleased

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
