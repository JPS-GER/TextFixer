param([switch]$Run, [string]$OutputDirectory = 'artifacts/TextFixer')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts/publish.ps1') -Target $OutputDirectory
if ($Run -and -not (Get-Process TextFixer -ErrorAction SilentlyContinue)) {
    $exe = Join-Path (Join-Path $PSScriptRoot $OutputDirectory) 'TextFixer.exe'
    Start-Process -FilePath $exe -WindowStyle Hidden
}
