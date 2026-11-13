<#
.SYNOPSIS
    Publishes LocalAssistant and registers/updates the "LocalAssistant Daily Email Digest"
    scheduled task: runs daily at 10:00 AM local time (system time zone is already
    Athens/Bucharest, so this is 10:00 Athens time), and catches up the run at next
    logon if the laptop was off/asleep at trigger time.

.NOTES
    Re-run this script any time after changing the code to republish + refresh the task.
    The task runs as the current Windows user while you are logged on.
#>

$ErrorActionPreference = "Stop"

$repoRoot    = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\LocalAssistant\LocalAssistant.csproj"
$publishDir  = Join-Path $repoRoot "src\LocalAssistant\bin\Publish"
$taskName    = "LocalAssistant Daily Email Digest"

Write-Host "Publishing $projectPath to $publishDir ..."
dotnet publish $projectPath -c Release -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exePath = Join-Path $publishDir "LocalAssistant.exe"
if (-not (Test-Path $exePath)) { throw "Published exe not found at $exePath" }

Write-Host "Registering scheduled task '$taskName' ..."

$action    = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $publishDir
$trigger   = New-ScheduledTaskTrigger -Daily -At "10:00AM"
$settings  = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -DontStopOnIdleEnd `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 15) `
    -RestartCount 2 `
    -RestartInterval (New-TimeSpan -Minutes 5)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "Summarizes unread Gmail via a local Ollama model and sends the digest to Telegram. Runs daily at 10:00 local time; if missed (laptop off/asleep), runs at next logon." `
    -Force | Out-Null

Write-Host "Done. Task '$taskName' will run daily at 10:00 (Athens time) and on next logon if a run was missed."
Write-Host "Inspect/adjust it any time in Task Scheduler, or remove it with:"
Write-Host "    Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false"
