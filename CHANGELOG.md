# Changelog

All notable changes to PromptForge are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **macOS app** (macOS 13+, Apple silicon and Intel): a menu bar app with the same optimise, selection and voice shortcuts (⌃⌥P, ⌃⌥⇧P, ⌃⌥D), review window, settings and quality check. Keys live in the login Keychain, start at login uses Login Items, and releases include `PromptForge-macOS-arm64.zip` and `PromptForge-macOS-x64.zip`. Build it with `installer/macos/build-app.sh`.
- Quality check: each case runs 3 times by default (`--repeat N` to change), and the report shows the pass rate per case, lowest first, plus how many cases passed every run, failed every run, or were mixed.
- Quality check: a judge model set separately in Settings → Data (Gemini by default, using the Gemini key from the Model tab). Without its key, the main model's fallback grades and the report says the judge isn't independent.
- Quality check: the judge also flags rewrites written as the AI's reply and requests the draft didn't make (extra deliverables, answer formats, "ask me first" steps), and its intent check covers statements, answers and standing instructions.
- `--system-prompt <file>` runs the quality check with another version of the rewriting rules, such as `eval/prompts/v1.0.0.txt`, without a new build.
- `--eval-compare before\results.json after\results.json compare.md` compares two runs case by case and separates real changes from run-to-run noise.
- Eval cases can carry the app hint (`writtenIn`), and 8 new cases cover the rewrites reported in #16, #17, #18 and #25–#28.
- Quality check: cases say how deep the ideal rewrite goes (`expect`) and what it must cover (`mustCover`), and a third of the new cases are held out (`--include-held-out`) for final measurements. 36 new cases cover agent tasks, mid-chat messages, the gaps found in real prompts and open-ended requests; `cases-write.json` holds briefs for the coming write mode.

### Fixed
- Quality check: runs that errored (rate limits, network failures) no longer count as failures. Comparisons leave them out, mark a case "not comparable" when errors leave it too few clean runs, and report how many runs errored on each side; pass rates in the report leave them out too.
- Quality check: a rate limit no longer turns the rest of the run into errors. The runner waits out short limits (up to 20 minutes, 3 times per call) and retries the same run; when the day's allowance is gone it stops cleanly, keeps what finished, and says which cases didn't run in `progress.txt` and the report.
- Quality check: a judged run the judge couldn't grade is now an error instead of a pass.

### Security
- An API key is never sent over plain `http://` to another machine: a custom or Ollama base URL must use `https://` unless it points at this computer or no key is set. Other schemes and URLs with a username or password are refused too.
- Updates are only downloaded from this repository's GitHub release URLs, and downloads are size-capped.
- Responses from model providers are capped at 16 MB, so a misbehaving endpoint can't exhaust memory.
- Dictated text has control characters removed before it is pasted (and line breaks too, in terminals), so it can't carry terminal escape sequences or run commands.
- On macOS and Linux, `settings.json` is written readable only by your account.

### Removed
- The phrase list that flagged rewrites written in the AI's voice. It only caught wordings we had thought of; the judge checks this now.

## [1.1.1] - 2026-10-15

### Fixed
- Voice now records from Windows' default recording device (and follows it if you change it). Before, it always used the first microphone in the device list, which on PCs with a headset, webcam or several mics could be the wrong one.
- The shortcut box in Settings recorded Ctrl+Alt+key as Ctrl+key on AltGr keyboard layouts, because Windows left Alt out of the keypress. It now reads the physical modifier keys too.
- Shortcuts that clash with your keyboard layout are now explained. On layouts where Ctrl+Alt acts as AltGr (German, Polish, French, English (India) and many more), Ctrl+Alt+key types a character: PromptForge now names the character at startup and before saving in Settings, instead of silently blocking it or failing.

### Changed
- Updated to NAudio 3's renamed recording API (`WaveIn`), removing the build warnings.

## [1.1.0] - 2026-10-15

### Added
- **Update check.** Once a day (and from **Check for updates** in the tray menu), PromptForge asks GitHub for the latest release. If it's newer, a notification offers to update: the exe is downloaded, verified against `SHA256SUMS.txt`, and installed through the usual **Yes** prompt. It can be turned off in Settings → General and sends no prompt text, settings or keys.
- **Image** task type: image-generation prompts (comma-separated descriptors, `--ar` style parameters) keep their form and only get typo fixes, instead of being rewritten into sentences.
- The user's own template slots, like `[Brand]`, `{{first_name}}` and `${Role:default}`, are now protected details and are checked after every rewrite.
- System prompt examples now include drafts that should come back unchanged.
- Eval: 9 new cases (image, placeholders, assistant-voice, already-good) and a check that flags rewrites written as the AI's reply.
- When a draft refers to material it doesn't include ("the attached file", "the code above", "the provided material"), the review window asks what it is instead of the rewrite guessing.
- Eval: the judge now flags dropped requirements and changed intent, and 10 new cases cover missing material, casually stated rules, and questions or requests that must keep their type.

### Changed
- Voice recording now uses NAudio 3.1 (was 2.4).
- Rules, criteria and conditions the user states are treated as requirements however casually they're written, and every item a draft asks about is kept.
- The rewrite keeps what kind of message the draft is: a question stays a question even with a word missing, and the requested action ("fix") isn't swapped for another ("explain"). Pasted text followed by a short request is treated as context, not as the task.

### Fixed
- Voice: a silent recording (microphone muted or turned off in Windows, or nothing said) was sent for transcription, and Whisper typed text it invented from the silence, usually "Thank you.". Silent recordings are now caught before sending, and a notification says whether the mic recorded nothing at all or just no speech.
- The repair pass for dropped details could make the model add an instruction like "Ensure the explanation includes the exact phrases…". It now asks for the details to be put back where they were, without meta-instructions.
- Rewrites could come back in the assistant's voice ("Once I have this information, I can..."). The system prompt now says the rewrite is always the user's request to the AI.
- Standard mode now puts any background the user gave before the request.

## [1.0.0] - 2026-10-13

First public release. It contains everything from the 0.x builds plus the fixes below.

### Added
- Open-source release under the MIT License, with contributing guide, security policy, issue templates and CI.
- Release builds are produced by GitHub Actions from version tags, with a SHA-256 checksum.

### Fixed
- Settings: **Cancel** and **Esc** now close the window. Before, only the title-bar close button did.

### Highlights
- Global shortcuts optimise the focused text box (<kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>P</kbd>) or the current selection (<kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd>), with a review window (improved / word diff / original) before anything is replaced.
- Replacement is verified by reading the field back. Windows 11 Notepad's delayed paste is handled with delayed clipboard rendering, and the clipboard is restored afterwards.
- The optimiser classifies the task, asks questions instead of inventing details, keeps exact details (with a repair pass), and holds back pasted code and traces so they come back byte-for-byte.
- Groq (free tier) by default, with an automatic fallback model. Gemini, Ollama and any OpenAI-compatible API are also supported.
- Voice dictation via Whisper, tap-to-toggle or hold-to-talk.
- The app installs and updates itself: no admin rights, a Start menu shortcut, and optional start with Windows.
- Built-in quality check over a 49-prompt test set.

## [0.3.1] - 2026-10-13

### Fixed
- Pasted material that the model placed mid-sentence is restored on its own lines.

## [0.3.0] - 2026-10-13

### Changed
- Vague drafts now get follow-up questions instead of coming back unchanged; no invented numbers; no questions the draft already answers.

## [0.2.0] - 2026-10-13

### Added
- Verified paste via delayed clipboard rendering, self-install/update, quality check from the tray, Settings brought to the front on relaunch.

## [0.1.0] - 2026-10-12

### Added
- Initial personal build: core optimiser, tray app, review window, settings, voice and eval runner.

[1.1.1]: https://github.com/Maneet7805/PromptForge/releases/tag/v1.1.1
[1.1.0]: https://github.com/Maneet7805/PromptForge/releases/tag/v1.1.0
[1.0.0]: https://github.com/Maneet7805/PromptForge/releases/tag/v1.0.0
[0.3.1]: https://github.com/Maneet7805/PromptForge/commits/main
[0.3.0]: https://github.com/Maneet7805/PromptForge/commits/main
[0.2.0]: https://github.com/Maneet7805/PromptForge/commits/main
[0.1.0]: https://github.com/Maneet7805/PromptForge/commits/main
