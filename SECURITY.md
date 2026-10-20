# Security policy

## Supported versions

Only the [latest release](https://github.com/Maneet7805/PromptForge/releases/latest) gets fixes. To update, click the update notification, or download the new release yourself (Windows: run the new `PromptForge.exe` and choose **Yes**; Mac: replace PromptForge.app in Applications).

## Reporting a vulnerability

**Please don't open a public issue for security problems.** Report them privately through [GitHub's private vulnerability reporting](https://github.com/Maneet7805/PromptForge/security/advisories/new).

Include what you found, the steps to reproduce it, and the impact you expect. You should get a reply within a week. Once a fix is released, you'll be credited in the release notes unless you'd rather not be.

## What PromptForge does with your data

This is useful context for judging a report:

- **API keys:** on Windows, encrypted with DPAPI (`CurrentUser` scope) in `%APPDATA%\PromptForge\secrets.dat`, so only your Windows account can decrypt them. On a Mac, stored as generic passwords in your login Keychain, with PromptForge as the only app on each item's access list.
- **Keys in transit:** a key is only sent over HTTPS, or over plain HTTP to `localhost` (a local Ollama or LM Studio). Base URLs with other schemes or embedded credentials are refused.
- **Prompt text** is sent only when you press a shortcut, and only to the provider you configured. It's never written to disk or logs. Dictated audio is kept in memory and sent only to your voice provider.
- **Logs** (Windows: `%APPDATA%\PromptForge\logs`; Mac: `~/Library/Logs/PromptForge`, owner-only; kept 7 days) record events and timings only.
- **Keystrokes:** PromptForge sends select-all, copy and paste (<kbd>Ctrl</kbd>/<kbd>⌘</kbd> + <kbd>A</kbd>/<kbd>C</kbd>/<kbd>V</kbd>) to the focused app. It refuses password fields, never sends keys to terminals, and never presses Enter or submits anything. Dictation pasted into a terminal is reduced to one line with control characters removed.
- **Mac permissions:** Accessibility (to send those keystrokes and to detect password fields and terminals) and, for voice, the microphone. Shortcuts use Carbon hotkeys, so PromptForge never needs Input Monitoring and never sees other keystrokes. It isn't sandboxed, because a sandboxed app can't use the Accessibility API on other apps.
- **Network:** the HTTPS calls to your configured model and voice providers, plus the daily update check to `api.github.com` (can be turned off in Settings → General; it sends no prompt text, settings or keys). There's no telemetry or server of our own.
- **Updates** are downloaded only from this repository's GitHub release URLs and checked against `SHA256SUMS.txt` from the same release. On a Mac, an update is installed automatically only when it is signed by the same Apple Developer team as the running app; otherwise you install it by hand.

## Unsigned builds

Release builds aren't code-signed (Windows) or notarized (Mac), so Windows SmartScreen warns on first run and macOS asks you to right-click → Open the first time. Every release lists a SHA-256 checksum in `SHA256SUMS.txt`. Verify your download:

```powershell
Get-FileHash .\PromptForge.exe -Algorithm SHA256
```

```bash
shasum -a 256 PromptForge-macOS-arm64.zip
codesign --verify --deep --strict PromptForge.app
```

The checksum file is published alongside the downloads, so it protects against corrupted or swapped mirrors, not against a compromised release. Code signing with a Developer ID (supported by `installer/macos/build-app.sh`) is what proves who built the app.

Releases are built from the tagged source by GitHub Actions. You can also build it yourself; see the README.
