<#
.SYNOPSIS
    Runs the Google "installed app" OAuth loopback flow once and prints a new
    GMAIL_REFRESH_TOKEN. Use this when the app needs a wider scope than the token you
    already have - e.g. after adding the "Digested" Gmail label feature, which needs
    'gmail.modify' (read + label) instead of 'gmail.readonly'.

.DESCRIPTION
    Reuses the GMAIL_CLIENT_ID / GMAIL_CLIENT_SECRET you already set as user
    environment variables (see the README). Opens your browser for consent, catches the
    redirect on a temporary localhost listener, exchanges the code, and writes the
    refresh token to the console.

    After running it:
      [Environment]::SetEnvironmentVariable("GMAIL_REFRESH_TOKEN", "<printed value>", "User")
    then open a NEW terminal and re-run scripts\Register-DailyDigestTask.ps1.

.PARAMETER Scope
    Space-separated OAuth scopes. Defaults to gmail.modify (the current app requirement).
#>

param(
    [string]$ClientId     = $env:GMAIL_CLIENT_ID,
    [string]$ClientSecret = $env:GMAIL_CLIENT_SECRET,
    [string]$Scope        = "https://www.googleapis.com/auth/gmail.modify"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ClientId))     { throw "GMAIL_CLIENT_ID is not set and -ClientId was not supplied." }
if ([string]::IsNullOrWhiteSpace($ClientSecret)) { throw "GMAIL_CLIENT_SECRET is not set and -ClientSecret was not supplied." }

# Grab a free loopback port (Google's Desktop client type accepts any localhost port).
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$probe.Start()
$port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port
$probe.Stop()

$redirectUri = "http://localhost:$port/"
$state       = [Guid]::NewGuid().ToString("N")

$authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + (@(
    "client_id=$([Uri]::EscapeDataString($ClientId))"
    "redirect_uri=$([Uri]::EscapeDataString($redirectUri))"
    "response_type=code"
    "scope=$([Uri]::EscapeDataString($Scope))"
    "access_type=offline"
    "prompt=consent"
    "state=$state"
) -join "&")

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($redirectUri)
$listener.Start()

Write-Host "Opening browser for Google consent (scope: $Scope) ..."
Write-Host "If it doesn't open, paste this into a browser:`n$authUrl`n"
Start-Process $authUrl

Write-Host "Waiting for the redirect on $redirectUri ..."
$context  = $listener.GetContext()
$request  = $context.Request
$code     = $request.QueryString["code"]
$gotState = $request.QueryString["state"]
$oauthErr = $request.QueryString["error"]

$html = "<html><body style='font-family:sans-serif'>You can close this tab and return to the terminal.</body></html>"
$buffer = [System.Text.Encoding]::UTF8.GetBytes($html)
$context.Response.ContentType = "text/html"
$context.Response.OutputStream.Write($buffer, 0, $buffer.Length)
$context.Response.OutputStream.Close()
$listener.Stop()

if ($oauthErr)            { throw "Google returned an OAuth error: $oauthErr" }
if ($gotState -ne $state) { throw "State mismatch - aborting (possible CSRF)." }
if (-not $code)           { throw "No authorization code in the redirect." }

Write-Host "Exchanging authorization code for tokens ..."
$tokenResponse = Invoke-RestMethod -Method Post -Uri "https://oauth2.googleapis.com/token" -Body @{
    client_id     = $ClientId
    client_secret = $ClientSecret
    code          = $code
    redirect_uri  = $redirectUri
    grant_type    = "authorization_code"
}

if (-not $tokenResponse.refresh_token) {
    throw "No refresh_token in the response. Revoke the app's access at https://myaccount.google.com/permissions and try again (Google only returns a refresh token on first consent)."
}

Write-Host ""
Write-Host "New GMAIL_REFRESH_TOKEN:" -ForegroundColor Green
Write-Host $tokenResponse.refresh_token
Write-Host ""
Write-Host "Set it (User scope) and refresh the scheduled task:" -ForegroundColor Cyan
Write-Host "  [Environment]::SetEnvironmentVariable(`"GMAIL_REFRESH_TOKEN`", `"$($tokenResponse.refresh_token)`", `"User`")"
Write-Host "  # open a NEW terminal, then:"
Write-Host "  powershell -File scripts\Register-DailyDigestTask.ps1"
