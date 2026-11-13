# Security Policy

LocalAssistant reads your email and holds Gmail and Telegram credentials, so
security problems are taken seriously.

## Supported versions

Only the latest commit on `main` receives security fixes.

## Reporting a vulnerability

**Please do not report security problems in public issues, discussions or pull
requests.**

Report them privately through GitHub:
[**Report a vulnerability**](https://github.com/MichaelGeranis/localassistant/security/advisories/new).

Please include what the problem is, steps to reproduce, and the impact you
expect. Never include real email content or credentials. LocalAssistant is
maintained by one person in their own time. The aim is to acknowledge a report
within 7 days and agree a disclosure date once a fix is understood. You will be
credited in the advisory unless you prefer not to be.

## Scope

Of particular interest:

- a Telegram user other than `TELEGRAM_CHAT_ID` getting a reply, or triggering
  any action;
- text in an email or note making the app mark, label or draft mail it should
  not, or write outside the configured Obsidian vault;
- credentials or email content reaching logs, the digest history, or anywhere
  other than Gmail, Telegram and the local Ollama server;
- HTML injection into the Telegram digest.
