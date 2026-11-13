# Contributing to LocalAssistant

Thanks for your interest. LocalAssistant is a small personal project, so issues
and focused pull requests are both welcome.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
Report security problems privately, as described in [SECURITY.md](SECURITY.md),
not in a public issue. **Never paste real email content, tokens or chat IDs
into an issue, test or log.**

## Development setup

You need the .NET 11 SDK (pinned in `global.json`). The code and tests run on
any OS. Only the scheduled-task scripts need Windows.

```bash
dotnet build LocalAssistant.slnx
dotnet test
dotnet format
```

To run the app itself you need a local Ollama server and the Gmail and Telegram
credentials described in the [README](README.md#getting-started).

## Rules the code follows

[CLAUDE.md](CLAUDE.md) explains the design in detail. In short:

- **No NuGet packages in the app.** External services are reached through the
  hand-written clients under `src/LocalAssistant/Integrations/`, with
  source-generated `System.Text.Json` contexts. Add new DTOs to the matching
  `[JsonSerializable]` list.
- **Settings keys are a contract.** Every setting has an explicit
  `[JsonPropertyName]`, a code default, an entry in `settings.example.json` and
  a row in the README. A test fails if the example and the code disagree.
- **Commands that change things never go through the model.** Parse them with
  plain rules, like `ChatCommandRouter` and `DigestNumberParser`.
- **Degrade, don't crash.** A failure in one email, note or chat turn is logged
  and skipped. Every path the app writes comes from `AppPaths`.
- **Digest numbers have one source:** `EmailDigest.NumberedItems`.

## Pull requests

- Keep each pull request to one change, with tests for any logic you add.
- `dotnet build`, `dotnet test` and `dotnet format --verify-no-changes` must
  pass. CI checks all three on Ubuntu and Windows.
- Use [Conventional Commits](https://www.conventionalcommits.org/) for commit
  messages (`feat:`, `fix:`, `docs:`, ...).
- Add a line under **Unreleased** in [CHANGELOG.md](CHANGELOG.md) for anything a
  user would notice.

By submitting a contribution you agree that it is licensed under the
[Apache License 2.0](LICENSE).
