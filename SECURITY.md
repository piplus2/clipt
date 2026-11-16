# Security policy

Clipt handles everything its users copy, including passwords and other secrets. Security reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue.** Report privately via this repository's **Security** tab →
**Report a vulnerability** (GitHub private vulnerability reporting).

Include what you found, how to reproduce it, and the impact you expect. You'll get an acknowledgement as soon as
possible, and a fix or mitigation plan once the issue is confirmed.

## Scope

**In scope:**
- plaintext clip content reaching disk, logs or the network
- weaknesses in the encryption or key handling
- secrets escaping masking, search exclusion, AI exclusion or expiry
- capture filters being bypassed
- copied content being executed

**Out of scope:** attacks that require malware already running as the same Windows user, or administrator access.
See the [threat model](docs/security.md#threat-model).

## Supported versions

Only the latest release receives security fixes.
