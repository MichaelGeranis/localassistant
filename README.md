# LocalAssistant

[![CI](https://github.com/MichaelGeranis/localassistant/actions/workflows/ci.yml/badge.svg)](https://github.com/MichaelGeranis/localassistant/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
![.NET 11](https://img.shields.io/badge/.NET-11-512BD4.svg)
![Status: personal project](https://img.shields.io/badge/status-personal_project-lightgrey.svg)

**A private email digest and chatbot that runs on your own machine.** Your mail
is summarised by a local model, so it never goes to a hosted AI service.

Every morning LocalAssistant reads your unread Gmail, has a local
[Ollama](https://ollama.com) model summarise and triage each message, and sends
you one Telegram message: what needs action, what is FYI, and how much is noise.
The same Telegram bot is also a private chatbot backed by the same local model.
From the chat you can mark digest items as read and save notes to an Obsidian
vault.

```
Unread email digest - 2026-11-13 10:00
7 unread · 2 need action · 3 FYI · 2 quiet

NEEDS ACTION
1. Dana asks you to confirm Thursday's design review slot by tomorrow.  ✍️ draft ready
2. Your domain renewal fails on Friday unless the card on file is updated.

FYI
3. The quarterly roadmap was shared; your team's items are on page 4.
4. Your package arrives today between 14:00 and 16:00.
5. The CI pipeline on main is green again after Tuesday's fix.

Quiet: 2 hidden (newsletters, receipts, notifications)

All unread
```

Each number links to its email in Gmail.

## Why

- **Private by design.** Summaries, triage, drafts and chat run on a local
  model. Only Gmail and Telegram are contacted, and only with your own
  credentials.
- **Predictable where it matters.** Triage uses Ollama's structured output, so
  every label is valid. Commands that change things (mark as read, save a note)
  are parsed with plain rules and never sent to the model.
- **Fails gracefully.** If Ollama is down you still get a digest of subjects.
  If a summary fails you lose that one line, not the whole digest. If the run
  crashes, you get a Telegram alert.
- **No dependencies.** Gmail, Telegram and Ollama are called through small
  hand-written `HttpClient` clients with source-generated JSON. The app has no
  NuGet packages.

## Features

| | |
|---|---|
| **Daily digest** | Unread Gmail, summarised and grouped into *Needs action*, *FYI* and *Quiet*, sent as one Telegram message. Digested mail gets a `Digested` label so it isn't digested again tomorrow. |
| **Draft replies** | For senders you list, the model writes a short reply and saves it as a Gmail draft in the thread (never sent), linked from the digest. |
| **Private chatbot** | `serve` mode long-polls your Telegram bot and replies with the local model, keeping a short rolling transcript. Messages from any other chat are dropped silently. |
| **Mark as read** | `/read 1 3-5` or `Gmail: mark the first and third as read` marks those digest items as read in Gmail. |
| **Obsidian notes** | `Obsidian: call the dentist` appends a timestamped line to your vault's inbox file. `Obsidian: digest` writes a summary of the whole vault, with `[[wikilinks]]` to each note. |
| **History** | Every digest is kept as a JSON line under `%LOCALAPPDATA%\LocalAssistant`, with a run log for unattended runs. |

## How it works

```
             ┌─────────────── daily scheduled task ───────────────┐
 Gmail ──► fetch unread ──► Ollama: summarise ──► Ollama: triage ──► Telegram digest
                                     │                   │
                                     └─ draft reply ─────┘ (listed senders only)
                                          └──► Gmail draft

             ┌─────────────── serve (logon task) ─────────────────┐
 Telegram ──► your chat only? ──► "Gmail:" / "/read"  ──► mark as read
                                  "Obsidian:"         ──► append note / vault digest
                                  anything else       ──► Ollama chat reply
```

[CLAUDE.md](CLAUDE.md) explains each step in detail.

## Getting started

### Requirements

- Windows 10 or 11 for the scheduled tasks. The app itself builds and runs on
  any OS with .NET.
- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- [Ollama](https://ollama.com/download), with a model pulled:

  ```powershell
  ollama pull llama3.2:3b
  ```

- A Google account, and a Telegram account if you want the digest delivered
  there. Without Telegram the digest is printed to the console.

### 1. Clone and build

```powershell
git clone https://github.com/MichaelGeranis/localassistant.git
cd localassistant
dotnet build
```

### 2. Connect Gmail

1. In the [Google Cloud console](https://console.cloud.google.com/), create a
   project, enable the **Gmail API**, and create an **OAuth client ID** of type
   *Desktop app*. Add your account as a test user on the consent screen.
2. Store the client ID and secret in Windows Credential Manager, then mint a
   refresh token and store it too. Each `secrets set` asks for the value without
   showing it; the script opens your browser for consent and prints the token:

   ```powershell
   dotnet run --project src/LocalAssistant -- secrets set GMAIL_CLIENT_ID
   dotnet run --project src/LocalAssistant -- secrets set GMAIL_CLIENT_SECRET
   powershell -File scripts/Get-GmailRefreshToken.ps1 -ClientId <client id> -ClientSecret <client secret>
   dotnet run --project src/LocalAssistant -- secrets set GMAIL_REFRESH_TOKEN
   ```

   The token carries the `gmail.modify` scope: read mail, add labels and create
   drafts. LocalAssistant never sends or deletes mail.

### 3. Connect Telegram (optional)

1. In Telegram, message [@BotFather](https://t.me/BotFather), send `/newbot`
   and copy the token.
2. Send your new bot any message, then find your chat ID:

   ```powershell
   dotnet run --project src/LocalAssistant -- secrets set TELEGRAM_BOT_TOKEN
   powershell -File scripts/Get-TelegramChatId.ps1 -BotToken <token>
   dotnet run --project src/LocalAssistant -- secrets set TELEGRAM_CHAT_ID
   ```

   The bot only ever answers this chat ID.

### 4. Run it

Then:

```powershell
dotnet run --project src/LocalAssistant            # one digest, now
dotnet run --project src/LocalAssistant -- serve   # the chatbot, until Ctrl+C
```

To run them unattended, register the scheduled tasks. Each script publishes a
Release build and creates or updates its task:

```powershell
powershell -File scripts/Register-DailyDigestTask.ps1   # daily at 10:00, catches up after sleep
powershell -File scripts/Register-ChatServiceTask.ps1   # at logon, restarts if it stops
```

## Configuration

Secrets stay in Windows Credential Manager (`secrets status` shows where each
one is kept). **Set up before this?** Run
`dotnet run --project src/LocalAssistant -- secrets migrate` once: it moves your
`GMAIL_*` and `TELEGRAM_*` environment variables into Credential Manager and
removes them. Everything else is in
`%LOCALAPPDATA%\LocalAssistant\settings.json`, which is created with defaults on
the first run. [settings.example.json](settings.example.json) shows every key:

| Key | Default | What it does |
|---|---|---|
| `model` | `llama3.2:3b` | Ollama model for summaries, triage, drafts and chat |
| `ollamaUrl` | `http://127.0.0.1:11434` | Ollama server, on this machine |
| `ollamaAllowRemote` | `false` | Allow an `ollamaUrl` on another machine (your mail goes there) |
| `digestLabel` | `Digested` | Gmail label marking digested mail |
| `includeQuiet` | `false` | List quiet mail instead of only counting it |
| `draftReplies.enabled`, `.senders` | `false`, `[]` | Draft replies for these senders: `jane@firm.com` or a whole domain, `@firm.com` |
| `chat.enabled` | `false` | Turn on `serve` mode |
| `chat.systemPrompt`, `.historyTurns`, `.longPollSeconds` | | Chat persona, transcript length, Telegram long-poll timeout |
| `obsidian.enabled`, `.vaultPath` | `false`, `""` | Turn on `Obsidian:` commands for a local vault folder |
| `obsidian.inboxFile`, `.digestFile` | `Inbox.md`, `Vault Digest.md` | Where notes go, and which file the vault digest overwrites |
| `telegram.digestDetail` | `full` | `minimal`: Telegram gets only counts and numbered links, no senders, subjects or summaries |
| `telegram.deleteAfterHours` | `0` | Delete digests, replies and your messages to the bot after this many hours (1–47; 23 or less if you only run the daily digest) |
| `history.retentionDays` | `90` | Days of digests kept in `digest-history.jsonl`; `0` keeps none |

A missing or broken settings file never stops a run: the app logs a line and
uses the defaults. A small model is fast and good enough for most mail. If
triage often misjudges ambiguous messages, try a 7–8B instruct model.

## Privacy and security

- Your email content goes to Gmail, your local Ollama server, and Telegram (only
  the digest you see). Nothing is sent to a hosted AI service, and `ollamaUrl`
  must be on this machine unless you set `ollamaAllowRemote`.
- **Telegram stores your chats on its servers; bot chats are not end-to-end
  encrypted.** Set `telegram.digestDetail` to `minimal` to keep senders, subjects
  and summaries off Telegram, and `telegram.deleteAfterHours` to delete digests
  and chat messages after a while.
- Secrets are in Windows Credential Manager, encrypted with your Windows logon,
  not in environment variables every program can read.
- The digest history keeps summaries, subjects and senders in plain text under
  your user profile, for `history.retentionDays` (90). It never keeps raw email
  bodies. Failure alerts carry only the error's type.
- The chatbot ignores every chat except `TELEGRAM_CHAT_ID`, without replying.
- Report vulnerabilities privately: see [SECURITY.md](SECURITY.md).

## Development

```powershell
dotnet build LocalAssistant.slnx
dotnet test
dotnet format
```

The tests cover command parsing, digest numbering and rendering, the draft
allowlist and the settings file contract. They make no network calls. CI runs
them on Ubuntu and Windows. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

LocalAssistant is licensed under the [Apache License 2.0](LICENSE).
