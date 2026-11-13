# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single Windows console app with two entry points, both talking to a **local** Ollama model:

- **No args** (default) — fetches unread Gmail, summarizes each message, and sends the digest
  to Telegram. Meant to run unattended as a daily Scheduled Task.
- **`serve`** — a resident loop that polls Telegram for messages sent to the bot and passes
  each straight through to the model (no tools, no email access beyond the `<Service>:`
  command prefixes described below): a private local chatbot. Meant to run unattended as a
  logon Scheduled Task, restarting on failure.

## Commands

```powershell
# Build / run during development (run from repo root)
dotnet build LocalAssistant.slnx
dotnet test                                  # unit tests (xUnit v3, any OS)
dotnet format                                # format; CI runs it with --verify-no-changes
dotnet run --project src/LocalAssistant

# Publish a Release build AND (re)register the daily Scheduled Task.
# Re-run after any code change you want the scheduled run to pick up.
powershell -File scripts/Register-DailyDigestTask.ps1

# Same, for the resident chat-passthrough loop ("serve" mode).
powershell -File scripts/Register-ChatServiceTask.ps1

# Run the chat loop directly in a terminal (foreground, Ctrl+C to stop) instead of the task.
dotnet run --project src/LocalAssistant -- serve

# Remove a task
Unregister-ScheduledTask -TaskName 'LocalAssistant Daily Email Digest' -Confirm:$false
Unregister-ScheduledTask -TaskName 'LocalAssistant Chat Service' -Confirm:$false

# Re-mint GMAIL_REFRESH_TOKEN (e.g. to widen the OAuth scope). Prints the new token.
powershell -File scripts/Get-GmailRefreshToken.ps1

# Find your Telegram chat id (after creating a bot with @BotFather and messaging it).
powershell -File scripts/Get-TelegramChatId.ps1
```

`tests/LocalAssistant.Tests` covers the pure logic (command parsing, digest numbering and
rendering, the draft allowlist, and the `settings.example.json` key contract); nothing in it
touches Gmail, Telegram or Ollama. Target framework is `net11.0` (SDK pinned in `global.json`).
The code has no Windows-only APIs and CI builds and tests it on Ubuntu and Windows, but the
scheduled-task scripts are PowerShell + Task Scheduler, so day-to-day use is on Windows.

## Configuration

Config splits by a **hard boundary**: **secrets** live in User-scoped environment variables;
**behaviour** lives in a JSON file. Nothing sensitive is ever in the JSON.

### Secrets — environment variables

Read once at process start. The `[Environment]::SetEnvironmentVariable(...)` commands that
set them are kept **outside the repo** (they hold live credentials); the README lists them.
`.gitignore` blocks `global variables.md`, `.env` and `*.secret` files in case a copy lands in
the tree.

- `GMAIL_CLIENT_ID`, `GMAIL_CLIENT_SECRET`, `GMAIL_REFRESH_TOKEN` — OAuth installed-app
  credentials; the client exchanges the refresh token for access tokens itself. The refresh
  token must carry the `https://www.googleapis.com/auth/gmail.modify` scope (read **and**
  label — the dedup label feature needs write access). Re-mint it any time with
  `powershell -File scripts/Get-GmailRefreshToken.ps1` (loopback consent flow, reuses the
  client id/secret), then set the var and re-run the task registration.
- `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID` — Telegram bot (one message, tap-through links,
  no truncation). Token from `@BotFather`; chat id via
  `powershell -File scripts/Get-TelegramChatId.ps1`.
- `OLLAMA_*` — server-side tuning, not read by this app; they configure the Ollama server
  process.
- `DIGEST_INCLUDE_QUIET` — **transitional.** `1`/`true`/`yes` still forces `includeQuiet` on;
  superseded by the JSON setting below and slated for removal.

Missing Gmail vars throw and abort the run. Telegram is optional: if both Telegram vars are
set the digest is sent there; if not, `Program.cs` just prints it to the console.

### Behaviour — `settings.json`

`Settings.Load()` (`src/LocalAssistant/Configuration/Settings.cs`) reads
`%LOCALAPPDATA%\LocalAssistant\settings.json` (next to the run log — outside the repo, and
outside `bin/Publish` so a republish never clobbers it). **Every field has a code default**,
so a missing / partial / malformed / null file just logs a line and uses defaults — a run
never fails over config. A missing file is (re)written as a template on first run.
`settings.example.json` in the repo root documents the shape.

**The JSON key is a contract, the C# property name is not.** Every property carries an explicit
`[JsonPropertyName]`, so renaming a property in `Settings.cs` can never move the key a
`settings.json` already on disk is written against. This is not decoration — a rename once did
exactly that and silently reverted five settings to their code defaults, invisible because
"every field has a default" makes a missing key indistinguishable from an absent one. For the
same reason `Load()` now logs any key in the file that didn't bind to a property (it diffs the
file against a re-serialization of what it loaded, so there's no second list of valid keys to
keep in sync). When you add a setting: add the attribute, and add the row below and to
`settings.example.json`.

| key | default | effect |
|---|---|---|
| `model` | `llama3.2:3b` | Ollama model for the summary + triage passes |
| `digestLabel` | `Digested` | label applied to digested mail; excluded from the next run's search (`GMailServiceClient` ctor arg) |
| `ollamaUrl` | `http://127.0.0.1:11434` | local Ollama base URL |
| `includeQuiet` | `false` | list the "quiet" group in full vs. collapse to a count |
| `draftReplies.enabled` / `.senders` | `false` / `[]` | auto-drafted replies for listed senders (see step 2) |
| `chat.enabled` | `false` | turns on the `serve`-mode passthrough loop; `false` makes `serve` idle without polling |
| `chat.longPollSeconds` | `25` | Telegram `getUpdates` long-poll timeout in `serve` mode - replies are near-instant since the call returns the moment a message arrives (capped at 50s) |
| `chat.systemPrompt` | *(see example)* | prepended to every chat prompt |
| `chat.historyTurns` | `10` | prior user/assistant turns kept in the rolling in-memory transcript |
| `obsidian.enabled` | `false` | turns on the `Obsidian:` chat commands (note + digest) |
| `obsidian.vaultPath` | `""` | local folder holding the vault; the command replies "not configured" while blank |
| `obsidian.inboxFile` | `Inbox.md` | file within the vault that notes are appended to (created if missing) |
| `obsidian.digestFile` | `Vault Digest.md` | file within the vault that `Obsidian: digest` **overwrites**; also the one file the digest's own read skips |

`draftReplies.senders` entries match case-insensitively against the address parsed from an
email's `From` header: `jane@firm.com` (exact) or `@firm.com` / `firm.com` (whole domain,
not subdomains).

Parsed with a source-gen `System.Text.Json` context (`SettingsJsonContext`), camelCase,
case-insensitive — no new packages.

## Architecture

Pipeline, all orchestrated by `src/LocalAssistant/Program.cs`:

1. `GMailServiceClient.GetUnreadEmails()` — pages through all `is:unread -label:Digested`
   messages, fetches each `format=full`, and reduces the MIME tree to plain text (`GetBody`
   prefers `text/plain`, falls back to stripping HTML).
2. `EmailDigestService.BuildAsync(gmail, ollama, settings)` — the core. Two Ollama calls per
   email (`settings.LLM` for all):
   - **pass 1** summarizes the truncated body (`ConciseSummary` preset).
   - **pass 2** normally *triages* into an `EmailPriority` (`Action` / `Fyi` / `Quiet`) from
     the summary + sender + subject only — a short, clean signal — via a few-shot rubric and
     Ollama structured output (`format` = a JSON Schema, so the label is always valid). Triage
     failure or an unparseable label defaults to `Fyi`.
   - **but** when `draftReplies.enabled` and the sender is on `draftReplies.senders`
     (`DraftAllowlist.Matches`), pass 2 is instead a *draft reply* (`Drafting` preset): the
     item is forced to `Action`, `TryDraftReplyAsync` writes a 2–4 sentence reply (with a
     `[AI draft - review before sending]` footer) and `GMailServiceClient.CreateDraftAsync`
     saves it as a Gmail draft in the email's thread (`In-Reply-To` + `threadId`; covered by
     the `gmail.modify` scope). The `DigestItem` gets a `DraftUrl` deep link. Any failure here
     is caught and logged — the item still appears, just without a draft link.

   Returns an `EmailDigest` of `DigestItem`s; `MessageIds` for step 7 is computed from them.
3. `EmailDigest` — immutable result with computed views. When `OllamaAvailable`, both views
   group items by priority — **NEEDS ACTION**, then **FYI**, then **QUIET** (collapsed to a
   count unless `includeQuiet`) — with a `N unread · X need action · …` counts line, and a
   trailing `All unread` link. An item with a `DraftUrl` gets a trailing `✍️ draft ready`
   link. When Ollama was unreachable they fall back to the old flat numbered subjects-only
   list. `FullText` is plain text (console); `TelegramHtml` is Telegram HTML — each item
   number is an `<a>` deep-link, group headers bold, `& < >` escaped. `NumberedItems` exposes
   the same (number, item) pairs `Render` prints — used to persist the state that lets `serve`
   mode's "mark N as read" command resolve a digest number back to a message id (see below).
4. Delivery — `Program.cs` sends to Telegram if configured:
   - `TelegramServiceClient.SendLongHtml(...)` — `sendMessage` with `parse_mode=HTML`, split
     on line boundaries under 3800 chars (Telegram's limit is 4096; normally one message).
     Checks the `ok` field in the response. All chunks attempted; throws listing failures.
5. `EmailDigestStore.AppendAsync(digest, channel, delivered)`
   (`src/LocalAssistant/Storage/EmailDigestStore.cs`) — appends one JSON line per run to
   `%LOCALAPPDATA%\LocalAssistant\digest-history.jsonl`: schema version, timestamp, channel
   (`telegram` / `console`), `delivered`, `ollamaAvailable`, the counts, the **rendered message
   exactly as handed to the channel** (`TelegramHtml`, or `FullText` for console), and the
   structured items (id, sender, subject, summary, priority as text, draft url). Written on
   **every** run — empty, degraded, and failed-to-send included — right after the delivery
   attempt and before labeling, so the history records what went out rather than the bookkeeping
   that follows. **Nothing reads it yet**; it exists so a later feature can query past digests
   without re-reading Gmail. Swallows all its own exceptions, `RunLog`-style, and logs a line
   either way — a bookkeeping write must never cost a delivered digest. Note this is the app's
   first copy of email content outside Gmail: summaries, subjects and senders in plaintext under
   the user profile — never raw bodies. No retention policy yet (one line per daily run).
6. On a successful delivery only, `LastEmailDigestStore.SaveAsync(digest)`
   (`src/LocalAssistant/Storage/LastEmailDigestStore.cs`) separately writes `EmailDigest.NumberedItems` to
   `%LocalAppData%\LocalAssistant\last-digest.json` (number → message id + subject) — a small,
   overwritten-each-run mapping, unlike `EmailDigestStore`'s full append-only history. This is the
   digest run and the `serve` chat loop talking to each other across two separate processes —
   see the chat section's mark-as-read command.
7. `GMailServiceClient.MarkDigestedAsync(ids)` — applies the `Digested` label (created on
   first use, visible in the Gmail sidebar) via `messages/batchModify`, so the next run's
   query skips these messages. Remove the label from a message to have it re-digested.
   `Program.cs` calls it only when the digest was **delivered** — Telegram sent it, or
   Telegram isn't configured so printing it was the delivery — **and** `OllamaAvailable`
   (a subjects-only run stays unlabelled so it retries with real summaries). If Telegram is
   configured but its send failed, labeling is skipped. Labeling failure is itself non-fatal
   (logged, worst case the emails reappear next run).

### Chat passthrough (`serve` mode)

`ChatLoop.RunAsync` (`src/LocalAssistant/Features/ChatPassthrough/ChatLoop.cs`), entered from `Program.cs` when `args[0]
== "serve"`. Otherwise pure passthrough — no email/calendar access, no tools — one local model,
two-way over Telegram instead of one-way digests, with the `<Service>:` command prefixes as the
exception (step 3a below):

1. Loop: `TelegramServiceClient.GetUpdatesAsync(offset, timeoutSeconds: settings.Chat.LongPollSeconds)`
   — a genuine Telegram long-poll (default 25s, `chat.longPollSeconds` in `settings.json`, capped
   at 50s to stay clear of the ~60s range where a proxy tends to kill an idle connection): the
   call blocks server-side and returns the instant a message arrives, so replies are near-instant
   without polling on a tight interval. The loop goes straight into the next call either way - no
   extra delay on the happy path, just a 5s backoff if a poll throws. `offset` is persisted to
   `%LocalAppData%\LocalAssistant\telegram-offset.txt` after each update so a restart resumes
   without replaying already-seen messages.
2. **Security-critical filter**: every update's `chat.id` is checked against
   `TelegramServiceClient.ChatId` (from `TELEGRAM_CHAT_ID`) *before* anything else happens. A
   mismatch is logged (id only, never the message text) and silently dropped — no reply, so a
   stranger who finds the bot on Telegram (bots are searchable; there is no platform-level
   allowlist) learns nothing. This is the entire access-control model for the feature; see
   `src/LocalAssistant/Features/ChatPassthrough/ChatLoop.cs` `HandleUpdateAsync`.
3. `/reset` clears the in-memory transcript; `/help` and `/start` print a one-line usage note.
3a. **The command-prefix exceptions.** `ChatCommandRouter.TryParse` (`src/LocalAssistant/Features/ChatPassthrough/ChatCommandRouter.cs`)
   recognizes one consistent grammar for every integration that acts on the outside world:
   `<Service>: <text>` (case-insensitive prefix, split on the first colon). A match never reaches
   the model or the transcript — these drive real mutations, so behaviour needs to be predictable
   rather than occasionally creative, the same reasoning as the digest's structured-output triage.
   No recognized prefix (or no colon at all) → ordinary chat, unchanged. Two services today:
   - **`Gmail: <text>`** (plus the bare `/read 1 3 5` shortcut, checked before the router since
     it's already unambiguous) — `DigestNumberParser.TryParseNumbers` (`src/LocalAssistant/Features/EmailDigest/DigestNumberParser.cs`)
     extracts digest numbers from the body: digit runs, ordinal words ("first", "second", …), and
     `N-M` / `N to M` ranges. A plain regex parser, not an LLM call, for the same predictability
     reason. It lives with the digest, not with the chat commands: what a digest number *means* is
     the digest's business, so a second caller doesn't have to reach into the chat feature to ask.
     Chat owns only the surface syntax — the `Gmail:` prefix, and `MarkReadCommand`'s `/read`
     shortcut (`src/LocalAssistant/Features/ChatPassthrough/MarkReadCommand.cs`), which strips the
     shortcut and hands the rest over. `ChatLoop.HandleMarkReadAsync` loads `%LocalAppData%\LocalAssistant\last-digest.json`
     via `LastEmailDigestStore.LoadAsync()`, resolves each number to a message id, and calls
     `GMailServiceClient.MarkAsReadAsync` (removes Gmail's `UNREAD` label via `messages/batchModify`
     — independent of the `Digested` label, which controls what's in the *next* digest, not
     read/unread state). Numbers with no match in the last digest are reported back and skipped;
     the rest are still marked. `ChatLoop.RunAsync` constructs its own `GMailServiceClient` at
     startup (same `GMAIL_*` env vars as the digest) — if that throws (Gmail not configured), the
     command replies that email access isn't available instead of crashing the loop. A `Gmail:`
     body with no numbers in it replies that the command wasn't understood, rather than silently
     falling through to chat.
   - **`Obsidian: <text>`** — `ObsidianVaultWriter.AppendAsync` (`src/LocalAssistant/Storage/ObsidianVaultWriter.cs`)
     appends a timestamped line to `obsidian.inboxFile` inside `obsidian.vaultPath`. An Obsidian
     vault is just a folder of plain markdown files, so this is nothing more than a local file
     append — no API, no credential, no new package (unlike Gmail/Telegram it lives in
     `settings.json`, not env vars, since a folder path isn't a secret). Requires
     `obsidian.enabled` and a non-blank `vaultPath` that exists on this machine (i.e. the vault is
     actually synced here via iCloud/OneDrive/Obsidian Sync/whatever) — otherwise the command
     replies that Obsidian isn't configured rather than creating a folder tree no one asked for.
     Writing here and writing a note directly in Obsidian on the same PC land in the same file, so
     a later feature that reads the vault (e.g. to organize scattered notes into a design doc)
     sees both without needing to know which path a given line came from.
   - **`Obsidian: digest`** — the one sub-command of the above, and the only command prefix that
     *does* use the model. `VaultDigestCommand.IsDigestRequest`
     (`src/LocalAssistant/Features/ChatPassthrough/VaultDigestCommand.cs`) matches a small set of whole
     phrases (`digest`, `vault digest`, `summarize the vault`, …) — whole-phrase only, so
     "Obsidian: digest the pitch section first" is still saved as a note rather than silently
     rebuilding the file. `VaultDigestService.BuildAsync`
     (`src/LocalAssistant/Features/VaultDigest/VaultDigestService.cs`) then mirrors the email pipeline's
     shape: `ObsidianVaultReader.ReadNotes` (`src/LocalAssistant/Storage/ObsidianVaultReader.cs`) walks the
     vault for `*.md` (skipping dot-folders like `.obsidian`, and the digest file itself — digesting
     a digest would compound the model's paraphrasing each run), newest first, capped at
     `ObsidianVaultReader.MaxNotes` (100) since each note costs a model call; **pass 1** summarizes
     each note (`ConciseSummary`, skipped outright for notes under 120 chars — a three-word note is
     already its own summary); **pass 2** writes one cross-note overview from *the summaries only*
     (`Drafting` — reused rather than adding a fifth preset, since a short piece of prose is exactly
     what it's tuned for), which is what lets the vault outgrow a single context window.
     `ObsidianVaultWriter.WriteDigestAsync` then **overwrites** `obsidian.digestFile` — a digest
     is a view of the vault's current state, so a stale copy underneath would only ever be wrong.
     Note titles render as `[[wikilinks]]`, making the file a working index inside Obsidian. The
     chat reply is the overview, not the whole file. Degradation as everywhere else: Ollama
     unreachable → "try again in a bit" before any work starts; one unreadable note or one failed
     summary is logged and skipped; a failed overview just drops that section.
   Anything else goes to `ChatService.ReplyAsync` (`src/LocalAssistant/Features/ChatPassthrough/ChatService.cs`),
   which folds a system prompt (`chat.systemPrompt`) + the rolling transcript
   (`chat.historyTurns` pairs) + the new message into one `/api/generate` prompt (same endpoint
   the digest uses) with the `Chat` `OllamaRequestOptions` preset and `keep_alive: "30m"` so
   casual back-and-forth doesn't reload the model each message. `settings.LLM` — the same
   model as the digest.
4. Degradation matches the digest's philosophy: Ollama unreachable → a plain "try again"
   reply, not silence; a send failure is logged and the turn is *not* added to history (so a
   reply the user never saw doesn't pollute future context); a poll failure is logged and the
   loop just tries again next interval — nothing here should ever crash the daemon, since a
   crash is what the scheduled task's restart policy exists to paper over, not the norm.

### Cross-cutting design choices

- **Graceful degradation is deliberate.** If Ollama is unreachable
  (`WaitUntilAvailableAsync`, 30s grace for logon races), `BuildAsync` skips the LLM
  entirely: each item's summary becomes `"{sender} - {subject}"`, priority becomes `Fyi`,
  `OllamaAvailable=false` propagates into every view (which then render the flat fallback
  list). A mid-run summary failure is caught per-email; a mid-run triage failure or a
  malformed label falls back to `Fyi`; a draft-reply failure (model or Gmail) just drops the
  draft link — one bad message never loses the digest.
- **Every path the app owns comes from `AppPaths`** (`src/LocalAssistant/AppPaths.cs`): one folder
  (`%LOCALAPPDATA%\LocalAssistant`) and one named field per file — settings, run log, digest
  history, last-digest state, messenger offset. Writers call `AppPaths.EnsureRoot()` right
  before writing rather than assuming someone else created the folder. Nothing else should
  spell out `Environment.SpecialFolder.LocalApplicationData`.
- **Digest numbering has exactly one source: `EmailDigest.NumberedItems`.** `Render` prints
  those numbers and `LastEmailDigestStore` persists them, so "mark 3 as read" — arriving in a
  different process, from `serve` mode — resolves to the message the user actually saw as 3.
  Deriving the numbering a second time anywhere would silently mark the wrong email; `Render`
  reads it rather than counting again.
- **`RunLog.Append`** writes an append-only trace to
  `%LocalAppData%\LocalAssistant\digest-run-log.txt`. This is the primary way to debug
  scheduled runs — Task Scheduler only records that the process exited. `RunLog` swallows
  all its own exceptions by design. `Program.cs` logs a final `Run FAILED: ...` for any
  unhandled exception before rethrowing.
- **Unhandled failures also ping Telegram.** `Program.cs`'s outer `catch` calls
  `TryAlertFailure` — a best-effort `⚠️ Email digest run failed` message (exception type +
  first 500 chars, machine, timestamp) so an unattended crash isn't silent. It builds its own
  `TelegramServiceClient`, and every error inside it is swallowed (returns quietly if Telegram
  isn't configured) so alerting never masks the real failure. The original exception is still
  rethrown, so Task Scheduler's retries fire — a bad run can produce up to 3 alerts. Only
  unhandled exceptions alert; the degraded paths above (Ollama down, Telegram send failed,
  labeling failed) are already visible in the digest or the run log.
- **Hand-rolled API clients in vendor-style namespaces.** `Google.Apis.Gmail.v1`,
  `Ollama.Apis.v1`, and `Telegram.Apis.v1` are *not* NuGet packages — they are this repo's
  own classes (`src/LocalAssistant/Integrations/**/*ServiceClient.cs`). `LocalAssistant.csproj` has zero
  `PackageReference`s. Everything is raw `HttpClient`.
- **JSON uses source-generated contexts** (`GMailJsonContext`, `OllamaJsonContext`,
  `TelegramJsonContext`, `SettingsJsonContext`, `EmailDigestStoreJsonContext`, `LastEmailDigestJsonContext`)
  — add any new DTO to the relevant `[JsonSerializable]` list or serialization fails at runtime.
- **Ollama tuning lives in code**, in four `OllamaRequestOptions` presets: `ConciseSummary`
  (bounded `num_predict`, low temperature, `presence_penalty` forced to 0) for the summary
  pass; `Triage` (`temperature` 0, tiny `num_predict`) for classification; `Drafting`
  (`temperature` 0.3, `num_predict` 400) for the reply draft; `Chat` (`temperature` 0.7,
  `num_predict` 800, larger `num_ctx` 8192 for the rolling transcript, no fixed seed) for
  `serve` mode. The three digest presets share `num_ctx` 4096 so the model isn't reloaded
  between passes within one run; `Chat` differs because `serve` is a long-lived process that
  pays a reload once, not per-message. Change context size or sampling
  params deliberately: a `num_ctx` that differs between passes reloads the model. The **model** is `settings.LLM` (default `llama3.2:3b`) — a small
  text-only model that's fast, skips the vision encoder, and is good enough for both passes;
  the triage weak spot is genuinely ambiguous mail, which the rubric steers to the safe `Fyi`
  bucket. Bumping to a 7–8B instruct model via `settings.json` is the lever if that bugs you.
  The triage call also sends `format` (a JSON Schema) so the label is grammar-constrained to
  a valid enum regardless of model. (`OllamaApiRequest.Model` defaults to `Settings.DefaultModel`,
  but that only applies to a `MakeRequest` caller that passes no model — the digest always
  passes `settings.LLM`.)

### Scheduled tasks

Both scripts publish the same build to `src/LocalAssistant/bin/Publish` — republish once, both tasks
pick it up (only `serve` needs a re-register too if you're iterating on chat behaviour, since
Task Scheduler already points at the published exe either way).

- `scripts/Register-DailyDigestTask.ps1` — "LocalAssistant Daily Email Digest": daily at 10:00
  local time, `-StartWhenAvailable` so a missed run (laptop asleep) fires at next logon. Runs
  as the interactive user, 15-minute limit, 2 retries.
- `scripts/Register-ChatServiceTask.ps1` — "LocalAssistant Chat Service": starts at logon,
  runs `LocalAssistant.exe serve`, no execution time limit (it's meant to run forever), restarts
  automatically (999 retries, 1-minute interval) if it ever exits. Safe to register before
  `chat.enabled` is `true` — it'll just idle/exit and get restarted on the same schedule until
  you flip the setting.
