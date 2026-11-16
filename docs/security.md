# Security & privacy design

A clipboard manager sees everything a person copies: passwords, tokens, private messages, card numbers.
This document describes how Clipt protects that data, what it does **not** protect against, and how each claim is tested.

## Threat model

**In scope.** Clipt aims to protect clipboard history against:

| Threat | Mitigation |
|---|---|
| Another Windows account on the same PC | History is encrypted with a key wrapped by DPAPI for the owning user |
| A stolen laptop, disk image, backup or synced folder | Nothing copied is stored in plaintext; the key can't be unwrapped elsewhere |
| Someone watching your screen share or recording | The launcher is excluded from screen capture |
| Secrets lingering long after they were needed | Detection, masking, short retention and clipboard wipe |
| Secrets leaking into other systems | Never sent to AI; marked private for Windows clipboard history and cloud sync |
| Shoulder-surfing | Secrets are masked until explicitly revealed; re-masked on close and on lock |
| Tampering with the database file | Authenticated encryption: modified rows fail to decrypt and are skipped |

**Out of scope.** Malware running **as the same user** while Clipt is running can read Clipt's memory, unwrap the key
through DPAPI as that user, or simply read the clipboard directly. No user-mode clipboard manager can prevent that.
Clipt also doesn't protect against an administrator who installs software on the machine.

## Encryption at rest

**Key management**

| Item | Detail |
|---|---|
| Master key | 256 random bits from the OS CSPRNG, generated on first run |
| Key storage | `vault.key`, the master key wrapped with **DPAPI** (`DataProtectionScope.CurrentUser`) plus app-specific entropy. Only the same Windows account on the same machine can unwrap it. |
| Derived keys | Two independent subkeys via **HKDF-SHA256**: one for encryption (`clipt/aes-gcm`) and one for fingerprints (`clipt/hmac`). The master key is zeroed after derivation. |

**Data encryption**

| Item | Detail |
|---|---|
| Cipher | **AES-256-GCM** with a fresh random 96-bit nonce per value and a 128-bit tag |
| Context binding | The column name is passed as associated data, so a ciphertext can't be moved to another column (for example `html` → `text`) without failing authentication |
| Blob format | `[version 1 byte][nonce 12][tag 16][ciphertext]` |

**What's encrypted:** the text, HTML, RTF, OCR text, source-app name, source-app path, every image file and its list thumbnail (`.thumb`).
The database keeps only non-identifying metadata in the clear: kind, timestamps, image dimensions, pinned/sensitive
flags and the generic reason label ("Looks like a password").

**Fingerprints:** duplicate detection uses **HMAC-SHA256** with the derived key, not a plain hash. Without the key,
the database can't be used to test "was this password ever copied?".

**Search:** clips are decrypted into memory at start-up and searched there. There is **no full-text index on disk**,
because an FTS index would be a plaintext copy of everything.

**Deletion:** SQLite `secure_delete` is on, so deleted rows are overwritten in place. The WAL is truncated after
deletions. Image files are deleted; they were only ever written encrypted.

**Key loss:** if the key can't be unwrapped (for example after an administrator resets the password), Clipt moves the
unreadable history aside and starts a new one. It never falls back to a weaker key.

**Implementation:** `Services/CryptoVault.cs`, `Services/ClipStore.cs`.

## Sensitive content

`Services/SensitiveDetector.cs` classifies each text clip before it's stored:

| Category | Detection |
|---|---|
| Private keys | PEM/OpenSSH/PGP private-key headers |
| API keys and tokens | Provider-specific formats: Anthropic, OpenAI, AWS, GitHub, GitLab, Slack, Google, Stripe, npm, SendGrid, Azure storage/SAS |
| Access tokens | JWT structure |
| Credentials in text | `scheme://user:pass@host`; `password=`, `api_key:`, `client_secret=` and similar assignments |
| Card numbers | 13–19 digits with a valid issuer prefix and **Luhn** checksum |
| Bank accounts | IBAN shape and **ISO 13616 mod-97** checksum |
| Passwords | Single short token, ≥3 character classes, Shannon entropy ≥ 2.8 bits/char, and not shaped like an identifier, path, e-mail, URL, version, colour, hex hash or UUID |

The last rule is a heuristic, tuned to avoid flagging code identifiers (`getUserById2`, `my_variable_1`), git SHAs,
UUIDs, phone numbers and versions. Users can correct it either way with **Ctrl+H**.

**What happens to a sensitive clip:**

1. **Storage:** its HTML/RTF is dropped and only plain text is kept (encrypted). With *Never save*, nothing is stored.
2. **Display:** it's masked in the list and preview. Reveal is per clip, never persisted, and reset when the launcher
   hides or the session locks.
3. **Search:** it's indexed only by its reason label and source app, never by content.
4. **AI:** it's refused inside `AiService.RunAsync`, independent of the UI.
5. **Paste:** the clipboard data carries `ExcludeClipboardContentFromMonitorProcessing`,
   `CanIncludeInClipboardHistory = 0` and `CanUploadToCloudClipboard = 0`, which keeps it out of Windows clipboard history,
   cloud clipboard and well-behaved clipboard managers. The clipboard is cleared after **30 s** if it still holds that content.
6. **Retention:** it's deleted after a configurable time since last use (default **10 min**), unless pinned.

## Capture filters

These copies are never recorded:

- Copies carrying the opt-out formats password managers set: `ExcludeClipboardContentFromMonitorProcessing`,
  `Clipboard Viewer Ignore`, or `CanIncludeInClipboardHistory = 0`.
- Copies whose source process executable is on the user's excluded-apps list (password managers by default).
- Copies made while the foreground window is a private browsing window (title markers used by Edge, Firefox, Brave
  and Opera). Chrome Incognito has no title marker and can't be detected this way.
- Copies made while recording is paused.
- Clipt's own writes to the clipboard.

## Other safeguards

- **Screen capture:** the launcher sets `WDA_EXCLUDEFROMCAPTURE` (configurable).
- **No execution of copied content:** file clips are revealed in Explorer, never launched. Links are opened only for
  `http`, `https`, `mailto` and `ftp`. Images are exported via *Save as*, never written to a temp file.
- **AI is opt-in:**
  - Content is sent only when the user runs an action on a specific clip.
  - Claude is reached over HTTPS via the official Anthropic SDK.
  - Ollama must be on the loopback interface or use `https`.
- **Secrets at rest in settings:** the Anthropic API key is DPAPI-protected in `settings.json`.
- **Least privilege:** Clipt runs unelevated. Autostart uses the per-user `Run` key.
- **Supply chain:** dependency versions are pinned. The transitive native SQLite build is overridden to a version
  without the known advisory (GHSA-2m69-gcr7-jv3q).

## Verification

`tests/Clipt.Tests` contains 47 tests that back the claims above, including:

- **Detection:** secrets in every category are detected, and a set of ordinary strings is **not** flagged (identifiers, hashes,
  UUIDs, paths, URLs, e-mails, versions, phone numbers, Luhn-invalid numbers).
- **Vault:**
  - every encryption uses a new nonce, and tampering and wrong-column decryption are rejected
  - another key can't decrypt
  - fingerprints are keyed
  - the key file is DPAPI-wrapped
- **No plaintext on disk:** after storing text, HTML, RTF, an image and OCR text, every byte of every file in the data
  folder is scanned for the original strings (UTF-8 and UTF-16), with a positive control proving the scan catches leaks.
- **Search and retention:** sensitive clips aren't searchable by content; they expire unless pinned; deleting a clip removes its image file and thumbnail.
- **Migration:** a v0.1 plaintext database is migrated, re-scanned for secrets, and destroyed.

Run them with `dotnet test tests/Clipt.Tests`.

## Reporting a vulnerability

Please report security issues privately through GitHub's **Report a vulnerability** button on the repository's
*Security* tab, not in a public issue. See [SECURITY.md](../SECURITY.md).
