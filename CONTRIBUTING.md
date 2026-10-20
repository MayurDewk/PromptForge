# Contributing to PromptForge

Thanks for helping. PromptForge is a small project, so the process is light.

## Reporting a bug

[Open a bug report](https://github.com/Maneet7805/PromptForge/issues/new?template=bug_report.yml) and include:
- your Windows version and the app you were typing in
- what you pressed, and what happened instead
- the relevant lines from the latest log: tray icon → *Open logs folder* → `promptforge-*.log`

Logs never contain prompt text or keys, but please check them before pasting anyway. **Never paste your API key** into an issue.

## Suggesting a feature

[Open a feature request](https://github.com/Maneet7805/PromptForge/issues/new?template=feature_request.yml). Describe the problem first, then the solution you have in mind.

## Development setup

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Any editor works: Visual Studio 2026, Rider or VS Code with C# Dev Kit.

```bash
git clone https://github.com/Maneet7805/PromptForge.git
cd PromptForge
dotnet build
dotnet test tests/PromptForge.Core.Tests
```

To try a local build without touching your installed copy:

```bash
dotnet publish src/PromptForge.App -c Release -o publish
publish\PromptForge.exe --portable
```

Quit the installed PromptForge first, because only one copy can run at a time.

### Where things live

| Area | Location |
| --- | --- |
| Rewriting rules (system prompt) | `src/PromptForge.Core/Optimization/SystemPrompt.cs` |
| Optimiser pipeline, masking, detail checks | `src/PromptForge.Core/Optimization/` |
| Provider client and presets | `src/PromptForge.Core/Llm/` |
| Text capture / paste / verification | `src/PromptForge.App/TextAccess/` |
| Review and settings windows | `src/PromptForge.App/UI/` |
| Quality test set | `eval/cases.json` (how to run and compare: `eval/README.md`) |

## Pull requests

1. Fork the repo and create a branch from `main`.
2. Keep each pull request to one change, and add or update tests in `tests/PromptForge.Core.Tests` for anything in `PromptForge.Core`.
3. Make sure `dotnet test` passes. CI runs it on Windows and macOS for every pull request.
4. If you change the system prompt or optimiser behaviour, run the quality check (Settings → Data → *Run quality check*) and paste the before/after pass counts in the pull request.
5. For app (UI or Win32) changes, say which apps you tested in. Notepad, a browser chat and Word cover most cases.
6. Add a line under an `Unreleased` heading in `CHANGELOG.md`.

### Code style

- Follow `.editorconfig`: file-scoped namespaces, 4-space indent, nullable enabled.
- Prefer clear names over comments. Comment *why*, not *what*.
- Never log prompt text, transcripts or keys.
- PromptForge must never auto-submit, and must never send keystrokes to terminals.

## Releasing (maintainers)

1. Update `<Version>` in `src/PromptForge.App/PromptForge.App.csproj` and move `Unreleased` notes in `CHANGELOG.md` under the new version.
2. Commit, then tag and push: `git tag -a v1.2.3 -m "v1.2.3" && git push --follow-tags`.
3. The [Release workflow](.github/workflows/release.yml) builds the exe, creates the GitHub release, and uses the changelog section as release notes.

## Code of conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md). By taking part you agree to it.
