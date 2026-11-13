$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'skills/hot-swap-update'
$destination = Join-Path $env:USERPROFILE '.codex/skills/hot-swap-update'
if (Test-Path -LiteralPath $destination) { throw 'The Codex skill already exists; nothing was overwritten.' }
Copy-Item -LiteralPath $source -Destination $destination -Recurse
Write-Host "Installed: $destination"
Write-Host 'The skill will be available on your next Codex turn.'
