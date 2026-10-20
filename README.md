<div align="center">

<img src="docs/images/icon.png" width="96" alt="PromptForge icon">

# PromptForge

**Turn the rough prompt you're typing into a clear one without leaving the app you're in.**

Type naturally, press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>P</kbd> (Mac: <kbd>⌃</kbd><kbd>⌥</kbd><kbd>P</kbd>), review the rewrite, press <kbd>Ctrl</kbd>+<kbd>Enter</kbd> (Mac: <kbd>⌘</kbd><kbd>↩</kbd>).

[![Latest release](https://img.shields.io/github/v/release/Maneet7805/PromptForge?label=download&color=6d4aff)](https://github.com/Maneet7805/PromptForge/releases/latest)
[![CI](https://github.com/Maneet7805/PromptForge/actions/workflows/ci.yml/badge.svg)](https://github.com/Maneet7805/PromptForge/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20%7C%20macOS%2013%2B-0078d4)
![.NET 10](https://img.shields.io/badge/.NET-10-512bd4)

[**Download**](https://github.com/Maneet7805/PromptForge/releases/latest) · [Quick start](#quick-start) · [How it works](#how-it-works) · [FAQ](#troubleshooting) · [Contributing](CONTRIBUTING.md)

<img src="docs/images/review-improved.png" width="640" alt="PromptForge review window showing an improved prompt, follow-up questions and what changed">

</div>

---

## Why

Most prompts are typed in a hurry: no deliverable, missing context, details buried in a run-on sentence. PromptForge is a small Windows tray app and Mac menu bar app that fixes that in place, following [Anthropic's prompt engineering guidance](https://docs.claude.com/en/docs/build-with-claude/prompt-engineering/overview). It never sends anything for you: you always see the rewrite first and decide.

## Features

- **Works where you type.** ChatGPT, Claude, Gemini, Perplexity, VS Code, Word, email, Notepad: any normal text box, with no browser extension or per-app plugin.
- **Review before replace.** See the improved prompt, a word-level diff and the original side by side. Edit it, re-run it, copy it, or keep your original.
- **Doesn't invent things.** When details are missing it asks you questions instead of making them up. Simple questions stay simple, and good prompts come back unchanged.
- **Keeps exact details.** URLs, file paths, identifiers, numbers and quoted text are checked after every rewrite. Pasted code, stack traces and logs are held back and restored byte-for-byte.
- **Your clipboard is safe.** It's saved before and restored after every operation.
- **Voice dictation.** Tap or hold a shortcut, speak, and the transcript is typed (or optimised) for you.
- **Free to run.** Uses [Groq's](https://console.groq.com) free API tier by default. No card, no account with us, no server.
- **Private by design.** Keys are encrypted with Windows DPAPI or kept in the macOS Keychain, and no prompt text is ever logged or stored.

<details>
<summary><b>See the word-level diff view</b></summary>
<br>
<img src="docs/images/review-changes.png" width="640" alt="Changes tab showing removed words in red and added words in green">
</details>

## Quick start

### Windows

1. **Get a free API key** at <https://console.groq.com/keys>. It takes about a minute, and no card is needed.
2. **Download [`PromptForge.exe`](https://github.com/Maneet7805/PromptForge/releases/latest/download/PromptForge.exe)** from the latest release and double-click it.
   - Windows SmartScreen may say *"Windows protected your PC"*, because the app isn't code-signed (signing needs a paid certificate). Click **More info → Run anyway**.
3. Choose **Yes** to install. PromptForge copies itself to `%LOCALAPPDATA%\Programs\PromptForge`, adds a Start menu shortcut, and can start with Windows. It doesn't need admin rights or a separate .NET install.
4. Settings opens on the **Model** tab. Paste your key, click **Test connection**, then **Save**.
5. Click into any text box, type a rough prompt, and press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>P</kbd>.

**Update:** from 1.1.0 on, PromptForge checks for a new release once a day and shows a notification. Click it (or **Update to …** in the tray menu), and it downloads the new exe, checks it against the release's `SHA256SUMS.txt`, and asks **Yes** to update. Your settings and keys are kept. You can also download the newer `PromptForge.exe` yourself, run it, and choose **Yes**. To turn the daily check off: Settings → General.
**Uninstall:** run `uninstall.cmd` from the release zip. It removes the app, the shortcut and start-with-Windows, and asks whether to delete settings, keys and logs.

### Mac

1. **Get a free API key** at <https://console.groq.com/keys>.
2. **Download** `PromptForge-macOS-arm64.zip` (Apple silicon: M1 and later) or `PromptForge-macOS-x64.zip` (Intel) from the [latest release](https://github.com/Maneet7805/PromptForge/releases/latest), unzip it, and drag **PromptForge.app** into **Applications**. It needs macOS 13 (Ventura) or later.
3. Release builds aren't notarized by Apple, so the first time, **right-click PromptForge.app → Open → Open**. (Or verify the zip against `SHA256SUMS.txt` first: `shasum -a 256 PromptForge-macOS-arm64.zip`.)
4. PromptForge appears in the menu bar, not the Dock. macOS asks you to allow it under **System Settings → Privacy & Security → Accessibility**: that's what lets it copy your text and paste the result. It never sees keystrokes other than its own shortcuts.
5. Settings opens on the **Model** tab. Paste your key (it's stored in your login Keychain), click **Test connection**, then **Save**. Turn on **Open PromptForge when you log in** in General if you like.
6. Click into any text box, type a rough prompt, and press <kbd>⌃</kbd><kbd>⌥</kbd><kbd>P</kbd>.

On a Mac, the same shortcuts use Control and Option: <kbd>⌃</kbd><kbd>⌥</kbd><kbd>P</kbd> optimise, <kbd>⌃</kbd><kbd>⌥</kbd><kbd>⇧</kbd><kbd>P</kbd> selection, <kbd>⌃</kbd><kbd>⌥</kbd><kbd>D</kbd> voice. Shortcuts must include <kbd>⌃</kbd> or <kbd>⌘</kbd>, because Option alone types characters. In the review window, use <kbd>⌘</kbd> where Windows uses <kbd>Ctrl</kbd> (<kbd>⌘</kbd><kbd>↩</kbd> replace, <kbd>⇧</kbd><kbd>⌘</kbd><kbd>C</kbd> copy, <kbd>⌘</kbd><kbd>R</kbd> re-run).

**Mac update:** when a new version is out, click the notification. The zip is checked against `SHA256SUMS.txt`; an app signed with a Developer ID is then only installed if it's signed by the same developer as your copy. Unsigned release builds can't prove that, so PromptForge shows the checked app in Finder for you to drag into Applications.
**Mac uninstall:** Settings → Data → **Delete all PromptForge data…** (removes settings, logs, Keychain keys and the login item), then drag PromptForge.app to the Bin.

## Usage

| Shortcut (changeable in Settings) | What it does |
| --- | --- |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>P</kbd> | Optimise everything in the text box you're typing in |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd> | Optimise only the selected text. Use this in Word, code editors and long documents |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>D</kbd> | Voice: tap to start and tap again to stop, or hold while talking and release |

In the review window:

| Key | Action |
| --- | --- |
| <kbd>Ctrl</kbd>+<kbd>Enter</kbd> | Replace your text with the (edited) improved prompt |
| <kbd>Esc</kbd> | Keep your original |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>C</kbd> | Copy the improved prompt instead |
| <kbd>Ctrl</kbd>+<kbd>R</kbd> | Re-run, e.g. after answering the questions or changing Mode or Task |
| <kbd>Ctrl</kbd>+<kbd>1</kbd> / <kbd>2</kbd> / <kbd>3</kbd> | Improved / Changes / Original |

**Modes:**
- *Light*: minimal edits.
- *Standard* (default): clarity, plus structure where it helps.
- *Detailed*: full structure for complex tasks.

Set the default mode from the tray icon. The tray menu also has *Copy last original text* as an undo safety net, and *Pause shortcuts*.

## Where it works

PromptForge reads and writes text by sending <kbd>Ctrl</kbd>+<kbd>A</kbd> / <kbd>C</kbd> / <kbd>V</kbd> to the focused app. It then reads the field back to confirm the replacement actually happened.

| Where | Status | Notes |
| --- | --- | --- |
| Browser AI chats (ChatGPT, Claude, Gemini, Perplexity) | ✅ Tested | |
| Notepad and other native Windows text boxes | ✅ Tested | Including Windows 11 Notepad's delayed paste |
| VS Code / Cursor chat panels | ✅ Tested | In the code editor itself, use *Optimise selection* |
| Microsoft Word | ✅ Tested | Use *Optimise selection*, because select-all would take the whole document |
| Voice dictation | ✅ Tested | Uses the same Groq key (Whisper) |
| Terminals (Windows Terminal, cmd, PowerShell, Claude Code / Codex CLI) | ⚠️ Copy only, by design | Keystrokes in a terminal could interrupt or run commands. Copy your prompt, press the shortcut, then paste the result yourself |
| Password fields | 🚫 Refused | Detected via UI Automation (Windows), or the Accessibility API and Secure Input (Mac) |
| Apps running as administrator | ❌ Not possible | Windows blocks input from normal apps into elevated ones. You get a message and can copy instead |
| Mac: Terminal, iTerm2, Warp, Ghostty and other terminals | ⚠️ Copy only, by design | Same as on Windows: copy, press the shortcut, paste yourself |
| Mac: apps without Accessibility permission | ❌ Until allowed | System Settings → Privacy & Security → Accessibility → PromptForge |

If a replacement can't be confirmed, the improved prompt goes to your clipboard and a notification tells you. Your original text is never lost.

## How it works

```mermaid
flowchart LR
    A[Hotkey] --> B[Capture text<br/>clipboard saved]
    B --> C[Mask code, traces,<br/>documents]
    C --> D[One LLM call<br/>JSON result]
    D --> E{Exact details<br/>kept?}
    E -- no --> F[One repair call]
    E -- yes --> G[Review window]
    F --> G
    G -- Ctrl+Enter --> H[Paste, verify,<br/>restore clipboard]
```

1. **Mask pasted material.** Fenced code, stack traces, logs and `<document>` blocks are swapped for `[[BLOCK_n]]` markers. They come back byte-for-byte, and the request uses fewer tokens.
2. **One model call** with a static system prompt (`src/PromptForge.Core/Optimization/SystemPrompt.cs`). The prompt is static so it can be cached. The call classifies the task, decides how much to change, rewrites, and lists missing details as questions, all as JSON.
3. **Check exact details.** URLs, paths, file names, identifiers, quoted text and numbers from your draft must survive. If any were dropped, one repair call fixes them; otherwise the review window warns you.
4. **Fallback.** If the main model is rate-limited or down, a second model with its own limits is tried.

The rewriting rules:
- Be clear and direct, and state the deliverable.
- Keep the user's reasons.
- Say what to do rather than what to avoid.
- Add structure only when there are several requirements.
- Put pasted material before the request.
- No shouting, no "world-class expert" filler, nothing invented.

## Quality

A test set of 76 prompts runs with code-graded checks, three times each by default, so a change shows up as a per-case pass rate rather than one lucky or unlucky run. It covers simple, vague, coding, debugging, research, writing, study, planning, image-generation, template placeholders, references to material that isn't included, casually stated rules, questions, answers, statements and standing instructions that must keep their type, strict constraints, already-good, long, prompt-injection and non-English drafts. The checks look for:
- dropped details (including the user's own `[slots]` and `{{slots}}`) and leftover markers
- inserted placeholders and filler phrases
- invented numbers and over-expansion
- rewriting prompts that were already good
- missing questions on vague drafts

With `--judge`, a second model also checks for answering instead of rewriting, invented details, dropped requirements (a rule or condition the user stated that the rewrite left out), changed intent (a question turned into an instruction, "fix" turned into "explain", a standing instruction turned into a one-off task, or pasted context turned into the task), rewrites written as the AI's reply, and added requests (deliverables, formats or "ask me first" steps the draft didn't ask for). Set the judge in **Settings → Data** to a different provider from the main model, so it isn't grading its own model family.

| Version | Passed all checks | Median / p95 latency |
| --- | --- | --- |
| 0.2 | 48/53 (91%) | 830 ms / 1.5 s |
| 0.3 – 1.0 | 50/53 (94%) | 914 ms / 1.7 s |

The tests used `openai/gpt-oss-120b` on Groq's free tier, with one run per case; later results use repeats. Run it yourself: **Settings → Data → Run quality check** and pick `eval/cases.json`. To compare two versions of the rewriting rules, see [eval/README.md](eval/README.md).

## Models and free-tier limits

| Provider | Default model | Notes |
| --- | --- | --- |
| **Groq** (default) | `openai/gpt-oss-120b`, fallback `openai/gpt-oss-20b` | Free tier: 30 requests/min, 1,000/day and 8K tokens/min per model. That comfortably covers personal use |
| Gemini | `gemini-3.5-flash-lite` | Free tier available. Google may use free-tier prompts to improve its products |
| Ollama | any local model | Fully offline, but slower and RAM-hungry |
| Custom | any OpenAI-compatible API | Set the base URL, model and key |

A typical rewrite uses about 1,200 input tokens, most of which is the cached system prompt, plus 150–300 output tokens. Free-tier limits change, so check your provider's docs.

## Privacy

- Your text never leaves your PC until you press a shortcut. Then only the text you chose goes to your model provider, and dictated audio goes to your voice provider.
- The update check (on by default, Settings → General) asks `api.github.com` once a day which release is newest. It sends no prompt text, settings or keys.
- API keys are encrypted with Windows DPAPI for your Windows account (`%APPDATA%\PromptForge\secrets.dat`).
- No prompt history is stored. Logs (`%APPDATA%\PromptForge\logs`, kept 7 days) contain events and timings only: never prompt text, transcripts or keys.
- Text PromptForge puts on the clipboard is marked to stay out of Windows clipboard history and cloud sync.
- On a Mac, keys are stored in your login Keychain, settings in `~/Library/Application Support/PromptForge` and logs in `~/Library/Logs/PromptForge`, both readable only by your account. Text it pastes is marked transient and concealed, so clipboard managers skip it. Dictated audio is held in memory only.
- API keys are only ever sent over HTTPS. A custom or Ollama address with plain `http://` is accepted only on this computer (`localhost`) or when no key is set.

## Troubleshooting

| Problem | Fix |
| --- | --- |
| Shortcut does nothing | Another app may own it. Settings → Shortcuts shows conflicts. Also check *Pause shortcuts* in the tray menu |
| "Couldn't read text here" | The field is empty, or the app runs as administrator. Select the text and use *Optimise selection*, or copy it manually |
| "Rate limit reached" | Wait a few seconds; the fallback model is used automatically |
| Result copied instead of replaced | Focus changed, the field changed, or the app rejected the paste. Press <kbd>Ctrl</kbd>+<kbd>V</kbd> |
| A shortcut types a character (e.g. <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>D</kbd> types "ḍ") | Your keyboard layout uses <kbd>Ctrl</kbd>+<kbd>Alt</kbd> as AltGr. PromptForge warns about this at startup and in Settings; pick a shortcut without <kbd>Ctrl</kbd>+<kbd>Alt</kbd>, e.g. <kbd>Alt</kbd>+<kbd>V</kbd> |
| Voice records the wrong microphone | PromptForge uses Windows' default recording device: Settings → System → Sound → Input |
| Voice: "Couldn't start the microphone" | Windows Settings → Privacy & security → Microphone → allow desktop apps |
| Anything else | Tray icon → *Open logs folder*, check the latest `promptforge-*.log`, and [open an issue](https://github.com/Maneet7805/PromptForge/issues/new/choose) |

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The code builds on Windows, Linux or macOS. The Windows app runs on Windows 10/11 x64; the Mac app runs on macOS 13+ (Apple silicon or Intel).

```bash
git clone https://github.com/Maneet7805/PromptForge.git
cd PromptForge
dotnet test tests/PromptForge.Core.Tests
dotnet publish src/PromptForge.App -c Release -o publish
```

The output is a single self-contained `publish/PromptForge.exe` (~72 MB). For the Mac app, run this on a Mac (it needs Xcode's command-line tools for `codesign`, `sips` and `iconutil`):

```bash
installer/macos/build-app.sh            # this Mac's processor; or pass arm64 / x64
open dist/macos-arm64/PromptForge.app
```

Set `SIGN_IDENTITY` to a Developer ID to sign with the hardened runtime, and `NOTARY_PROFILE` to notarize; see the script header. Releases are built by [GitHub Actions](.github/workflows/release.yml) whenever a `v*` tag is pushed.

```
src/PromptForge.Core     optimiser pipeline, provider client, settings, eval runner (cross-platform, unit-tested)
src/PromptForge.App      Windows tray app: hotkeys, text capture/replace, review window, settings, voice, self-install
src/PromptForge.Mac      Mac menu bar app (Avalonia UI + AppKit, Carbon, Accessibility, Keychain, CoreAudio)
tests/                   unit tests for the core
eval/cases.json          optimiser test set
installer/               Windows uninstall script; macOS app bundle, entitlements and build script
```

## Contributing

Bug reports, ideas and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). Please report security issues privately as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © 2026 Maneet Arvind Mehta

## Acknowledgements

- [Anthropic's prompt engineering guide](https://docs.claude.com/en/docs/build-with-claude/prompt-engineering/overview), the basis of the rewriting rules
- [Groq](https://groq.com) for the free inference tier
- [DiffPlex](https://github.com/mmanela/diffplex) (word diff), [NAudio](https://github.com/naudio/NAudio) (Windows microphone capture) and [Avalonia](https://avaloniaui.net) (Mac UI)
