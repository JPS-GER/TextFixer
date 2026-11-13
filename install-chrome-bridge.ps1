param([string]$ExtensionId, [string]$ExePath)
$ErrorActionPreference = 'Stop'
if (-not $ExtensionId) { $ExtensionId = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'chrome-extension/extension-id.txt') -Raw).Trim() }
if ($ExtensionId -notmatch '^[a-p]{32}$') { throw 'Invalid Chrome extension ID.' }
if (-not $ExePath) { $ExePath = Join-Path $PSScriptRoot 'artifacts/TextFixer/TextFixer.exe' }
$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$stateFolder = Join-Path $env:LOCALAPPDATA 'TextFixer/Chrome'
New-Item -ItemType Directory -Force $stateFolder | Out-Null
$manifestPath = Join-Path $stateFolder 'chrome-native-host.json'
$manifest = @{ name = 'com.textfixer.desktop'; description = 'TextFixer desktop connection'; path = $resolvedExe; type = 'stdio'; allowed_origins = @("chrome-extension://$ExtensionId/") }
$json = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))
$registryKey = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.textfixer.desktop'
New-Item -Path $registryKey -Force | Out-Null
Set-Item -Path $registryKey -Value $manifestPath
Write-Host 'Registered TextFixer for the current Windows user.'
Write-Host "Extension ID: $ExtensionId"
Write-Host 'In Chrome, open chrome://extensions, enable Developer mode, choose Load unpacked, and select:'
Write-Host (Join-Path $PSScriptRoot 'chrome-extension')
Write-Host 'Then select text inside an editor, right-click, and choose Review selection in TextFixer.'
