# Runs Rahiq on a laptop without Docker.
#   powershell -ExecutionPolicy Bypass -File infra\scripts\run-local.ps1
# Database: a portable PostgreSQL 17 in %USERPROFILE%\rahiq-local\pg (already migrated and seeded; user rahiq / rahiq_dev).
# E-mails (customer sign-in codes) are written to the API window instead of being sent.
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$pg = "$env:USERPROFILE\rahiq-local\pg"

function Test-Port($port) { [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) }

if (-not (Test-Port 5432)) {
    if (-not (Test-Path "$pg\package\native\bin\pg_ctl.exe")) { throw "PostgreSQL not found in $pg" }
    Write-Host "Starting PostgreSQL" -ForegroundColor Cyan
    Remove-Item "$pg\data\postmaster.pid" -ErrorAction SilentlyContinue
    # Started detached: pg_ctl's server would otherwise keep this window's output open.
    Start-Process "$pg\package\native\bin\pg_ctl.exe" -ArgumentList "-D", "`"$pg\data`"", "-l", "`"$pg\pg.log`"", "-o", "`"-p 5432`"", "start" -WindowStyle Hidden
    for ($i = 0; $i -lt 30 -and -not (Test-Port 5432); $i++) { Start-Sleep 1 }
}

if (-not (Test-Port 5080)) {
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "`$host.UI.RawUI.WindowTitle='Rahiq API'; `$env:ASPNETCORE_ENVIRONMENT='Development'; `$env:Email__Transport='log'; cd '$root\backend'; dotnet run --project src\Rahiq.Api --urls http://localhost:5080"
}
if (-not (Test-Port 3000)) {
    Start-Process powershell -ArgumentList "-NoExit", "-Command", "`$host.UI.RawUI.WindowTitle='Rahiq Web'; cd '$root\frontend'; pnpm dev"
}

Write-Host "Starting... (the first page load takes ~20 s while Next.js compiles)" -ForegroundColor Cyan
for ($i = 0; $i -lt 90; $i++) {
    try { Invoke-WebRequest http://localhost:3000/robots.txt -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep 2 }
}
Start-Process "http://localhost:3000/tr"
Write-Host "Store: http://localhost:3000/tr   Admin: http://localhost:3000/tr/admin/login" -ForegroundColor Green
