<#
.SYNOPSIS
    Prints the Telegram chat id(s) that have messaged your bot, so you can set
    TELEGRAM_CHAT_ID for the digest.

.DESCRIPTION
    One-time setup:
      1. In Telegram, open @BotFather -> /newbot -> follow the prompts -> copy the token.
      2. [Environment]::SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", "<token>", "User")
         (or pass -BotToken to this script).
      3. Open your new bot in Telegram and send it any message (e.g. "hi").
      4. Run this script. It calls getUpdates and lists the chat id(s) it finds.
      5. [Environment]::SetEnvironmentVariable("TELEGRAM_CHAT_ID", "<id>", "User")
      6. Open a NEW terminal and re-run scripts\Register-DailyDigestTask.ps1.

    For a private chat the id is your user id (a positive number). For a group it is a
    negative number - add the bot to the group and send a message there instead.
#>

param(
    [string]$BotToken = $env:TELEGRAM_BOT_TOKEN
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($BotToken)) {
    throw "TELEGRAM_BOT_TOKEN is not set and -BotToken was not supplied. Create a bot with @BotFather first."
}

$response = Invoke-RestMethod -Method Get -Uri "https://api.telegram.org/bot$BotToken/getUpdates"

if (-not $response.ok) {
    throw "Telegram API returned an error: $($response | ConvertTo-Json -Depth 5)"
}

if (-not $response.result -or $response.result.Count -eq 0) {
    Write-Warning "No updates. Send your bot a message in Telegram, then run this again."
    Write-Warning "(getUpdates only returns messages from roughly the last 24h and none that a webhook already consumed.)"
    return
}

$chats = $response.result |
    ForEach-Object { $_.message.chat, $_.channel_post.chat, $_.my_chat_member.chat } |
    Where-Object { $_ } |
    Sort-Object id -Unique

Write-Host ""
Write-Host "Chats that have interacted with this bot:" -ForegroundColor Green
foreach ($chat in $chats) {
    $label = @($chat.title, "$($chat.first_name) $($chat.last_name)".Trim(), $chat.username) |
        Where-Object { $_ } | Select-Object -First 1
    Write-Host ("  {0,-16} {1} ({2})" -f $chat.id, $label, $chat.type)
}

Write-Host ""
Write-Host "Set the one you want (User scope) and refresh the scheduled task:" -ForegroundColor Cyan
Write-Host "  [Environment]::SetEnvironmentVariable(`"TELEGRAM_CHAT_ID`", `"$($chats[0].id)`", `"User`")"
Write-Host "  # open a NEW terminal, then:"
Write-Host "  powershell -File scripts\Register-DailyDigestTask.ps1"
