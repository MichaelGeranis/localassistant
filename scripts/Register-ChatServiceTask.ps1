<#
.SYNOPSIS
    Publishes LocalAssistant and registers/updates the "LocalAssistant Chat Service" scheduled
    task: starts at logon, runs `LocalAssistant.exe serve` (the Telegram chat-passthrough loop)
    indefinitely, and restarts it automatically if it ever exits unexpectedly.

.NOTES
    Re-run this script any time after changing the code to republish + refresh the task.
    Shares the same published build as the daily digest task
    (Register-DailyDigestTask.ps1) - the two tasks just point at different entry args.

    Chat passthrough only does anything when chat.enabled is true in settings.json
    (%LOCALAPPDATA%\LocalAssistant\settings.json). Registering this task with chat still
    disabled is harmless: the process starts, logs "chat disabled", and exits - Task
    Scheduler's restart policy then just quietly retries it on the same schedule below.
#>

$ErrorActionPreference = "Stop"

$repoRoot    = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\LocalAssistant\LocalAssistant.csproj"
$publishDir  = Join-Path $repoRoot "src\LocalAssistant\bin\Publish"
$taskName    = "LocalAssistant Chat Service"

Write-Host "Publishing $projectPath to $publishDir ..."
dotnet publish $projectPath -c Release -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exePath = Join-Path $publishDir "LocalAssistant.exe"
if (-not (Test-Path $exePath)) { throw "Published exe not found at $exePath" }

Write-Host "Registering scheduled task '$taskName' ..."

# -User is required on the trigger: an AtLogOn trigger with no -User fires at ANY user's
# logon, which Task Scheduler treats as a machine-wide trigger and refuses to register without
# admin rights ("Access is denied"), even though the task itself only ever runs as this user
# (-RunLevel Limited below). Scoping the trigger to $env:USERNAME keeps registration unelevated -
# this is the one thing that differs from the daily digest task's plain time-based trigger.
$action    = New-ScheduledTaskAction -Execute $exePath -Argument "serve" -WorkingDirectory $publishDir
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# AllowStartIfOnBatteries/-DontStopIfGoingOnBatteries: New-ScheduledTaskSettingsSet defaults
# both battery switches to "off" (i.e. DisallowStartIfOnBatteries=true), which is right for a
# task like the daily digest but wrong here - on battery power (the common case on a laptop),
# Task Scheduler would queue this task forever and never actually launch it, which looks
# identical to a hang: State stays "Queued", LastTaskResult stays a stale success code, and
# nothing shows up in the run log because the process never starts.
$settings  = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -DontStopOnIdleEnd `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "Resident Telegram chat passthrough to the local Ollama model. Starts at logon; long-polls for new messages (chat.longPollSeconds in settings.json). Restarts automatically if it exits." `
    -Force -ErrorAction Stop | Out-Null

Write-Host "Done. Task '$taskName' will start at next logon (or now, if you start it manually)."
Write-Host "Start it immediately without logging off:"
Write-Host "    Start-ScheduledTask -TaskName '$taskName'"
Write-Host "Stop it:"
Write-Host "    Stop-ScheduledTask -TaskName '$taskName'"
Write-Host "Remove it:"
Write-Host "    Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false"
Write-Host ""
Write-Host "Remember: chat only responds once chat.enabled=true in"
Write-Host "    $env:LOCALAPPDATA\LocalAssistant\settings.json"
