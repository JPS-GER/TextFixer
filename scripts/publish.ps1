param([string]$Target = 'artifacts/TextFixer')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
$publishLock = $null
try {
    $publishLock = [IO.File]::Open((Join-Path $projectRoot '.build.lock'), 'CreateNew', 'Write', 'None')
    $stage = Join-Path $projectRoot ('artifacts/build-' + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
    $arguments = @('publish', 'src/TextFixer/TextFixer.csproj', '-c', 'Release', '-o', $stage, '--nologo', '-p:NuGetAudit=false')
    if (Test-Path '.local/packages/microsoft.web.webview2.1.0.4314.35.nupkg') { $arguments += "-p:RestoreSources=$projectRoot/.local/packages" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; installed files were not changed.' }
    $destination = [IO.Path]::GetFullPath((Join-Path $projectRoot $Target))
    # Preserve the old loose UI and old manifest for the transition from pre-embedded builds.
    & "$PSScriptRoot/publish-swap.ps1" -From $stage -Target $destination -WorkspaceRoot $projectRoot -PreserveNames @('Web', 'chrome-native-host.json') -SkipScheduledCleanup
    Write-Host 'The running app was left open. The next launch uses the new build.'
}
finally {
    if ($publishLock) { $publishLock.Dispose(); Remove-Item -LiteralPath (Join-Path $projectRoot '.build.lock') }
    Pop-Location
}
