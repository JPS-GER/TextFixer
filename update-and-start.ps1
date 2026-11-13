$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts/publish.ps1')
if (-not (Get-Process TextFixer -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath (Join-Path $PSScriptRoot 'artifacts/TextFixer/TextFixer.exe') -WindowStyle Hidden
}
