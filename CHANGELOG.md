# Changelog

All notable changes to LocalAssistant are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- Secrets live in Windows Credential Manager. `LocalAssistant secrets migrate`
  moves existing `GMAIL_*` and `TELEGRAM_*` environment variables there and
  removes them; `secrets set` and `secrets status` manage them. Environment
  variables are still read, with a log line, until migrated.
- `telegram.digestDetail: "minimal"` sends Telegram only the counts and
  numbered links, no senders, subjects or summaries.
- `telegram.deleteAfterHours` deletes digests, alerts, chat replies and the
  messages typed to the bot once they are that old.
- `history.retentionDays` (default 90) prunes `digest-history.jsonl`; `0` keeps
  no history.
- Apache-2.0 licence, `NOTICE`, README, contributing guide, security policy,
  code of conduct, and issue and pull request templates.
- Unit tests (xUnit v3) for command parsing, digest numbering and rendering,
  the draft-reply allowlist and the `settings.example.json` key contract.
- CI on Ubuntu and Windows: build, tests and `dotnet format`, with actions
  pinned to commit SHAs and weekly Dependabot updates.
- `LocalAssistant.slnx` solution and a `global.json` pinning the SDK.

### Changed

- `ollamaUrl` must be on this machine unless `ollamaAllowRemote` is set; any
  other URL is ignored, logged, and the default used.
- Failure alerts to Telegram carry only the exception's type, and the run log
  no longer records email subjects or note paths.

- **.NET 11** (from .NET 10). The app now targets `net11.0` instead of
  `net10.0-windows`: it used no Windows-only APIs. The scheduled tasks still
  need Windows.
- The project moved from `ConsoleApp/` to `src/LocalAssistant/`, and the
  published executable is now `LocalAssistant.exe`. Re-run both
  `Register-*Task.ps1` scripts once so the tasks point at the new executable.
- The scripts folder is now `scripts/`.
- Warnings are treated as errors.

### Before this changelog

- Daily Gmail digest summarised and triaged by a local Ollama model, delivered
  to Telegram, with a `Digested` label so mail is digested once.
- Draft replies saved to Gmail for listed senders.
- `serve` mode: a private Telegram chatbot on the local model, with
  `/read` / `Gmail:` mark-as-read and `Obsidian:` note and vault digest
  commands.
- Digest history, run log and Telegram alerts on unhandled failures.
