# Puts a new build of a Windows app into its install folder, also while the app is running.
#
# A running exe can't be overwritten or deleted, but it can be renamed (same drive): every current item is renamed to
# <name>.old-<yyyyMMdd-HHmmss>, the new files move in, and the next start of the app runs the new version.
# Old versions are deleted right away when nothing uses them, otherwise marked for deletion at the next reboot
# (MoveFileEx, needs an elevated shell) or, without admin rights, at the next sign-in (HKCU RunOnce).
#
# Build with dotnet publish (single-file, self-contained):
#   powershell -ExecutionPolicy Bypass -File scripts\publish-swap.ps1 -Project src\App\App.csproj -Target publish\App
# Or swap in output that another tool already built (Tauri, Electron, C++ …):
#   powershell -ExecutionPolicy Bypass -File scripts\publish-swap.ps1 -From src-tauri\target\release\bundle-dir -Target C:\Tools\App

param(
    [string]$Target,                       # install folder (created if missing)
    [string]$Project,                      # .csproj to publish; or use -From
    [string]$From,                         # folder with a finished build to swap in
    [string]$Runtime = 'win-x64',
    [string[]]$PublishArgs = @(),          # extra dotnet publish arguments
    [string[]]$PreserveNames = @(),
    [switch]$SkipScheduledCleanup,
    [string]$WorkspaceRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
if (-not $Target) { throw 'Give -Target (the install folder).' }
if (-not $Project -and -not $From) { throw 'Give -Project (a .csproj to publish) or -From (a finished build folder).' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
$oldPattern = '\.old-\d{8}-\d{6,9}'
$workspacePath = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
function Assert-WorkspacePath([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($workspacePath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the project workspace: $resolved"
    }
    return $resolved
}
$Target = Assert-WorkspacePath $Target

Add-Type -Namespace Native -Name Kernel32 -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool MoveFileEx(string existing, string replacement, int flags);
'@
$DelayUntilReboot = 4

# --- Build into a staging folder next to the target (same drive, so moves are renames) ---------
New-Item -ItemType Directory -Force $Target | Out-Null
$Target = (Resolve-Path $Target).Path
$staging = Join-Path (Split-Path $Target -Parent) ("$(Split-Path $Target -Leaf).staging-$stamp")
$staging = Assert-WorkspacePath $staging
if (Test-Path -LiteralPath $staging) { throw 'The staging directory already exists.' }

if ($Project) {
    dotnet publish $Project -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -o $staging -nologo -v q @PublishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}
else {
    if (-not (Test-Path $From)) { throw "Build folder not found: $From" }
    $source = Assert-WorkspacePath ((Resolve-Path -LiteralPath $From).Path)
    if ($source -eq $Target -or $source.StartsWith($Target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Build source must be outside the target directory.' }
    Copy-Item -LiteralPath $source -Destination $staging -Recurse
}
# Debug symbols and leftovers from the build don't belong in the install folder.
Get-ChildItem -LiteralPath $staging -Recurse -Filter *.pdb -File | ForEach-Object {
    $symbolPath = Assert-WorkspacePath $_.FullName
    Remove-Item -LiteralPath $symbolPath -Force
}

# --- Retire the current version: rename, never overwrite ---------------------------------------
foreach ($item in Get-ChildItem -LiteralPath $Target -Force | Where-Object { $_.Name -notmatch $oldPattern -and $_.Name -notin $PreserveNames }) {
    $old = if ($item.PSIsContainer) { "$($item.Name).old-$stamp" } else { "$($item.BaseName).old-$stamp$($item.Extension)" }
    $currentPath = Assert-WorkspacePath $item.FullName
    Rename-Item -LiteralPath $currentPath -NewName $old
    Write-Host "Renamed  $($item.Name) -> $old"
}

# --- Move the new version in ------------------------------------------------------------------
foreach ($item in Get-ChildItem -LiteralPath $staging -Force) {
    $moveSource = Assert-WorkspacePath $item.FullName
    $moveTarget = Assert-WorkspacePath (Join-Path $Target $item.Name)
    if ($item.Name -in $PreserveNames -and (Test-Path -LiteralPath $moveTarget)) { throw 'A staged item conflicts with a preserved file.' }
    Move-Item -LiteralPath $moveSource -Destination $moveTarget
    Write-Host "New      $($item.Name)"
}
Remove-Item -LiteralPath (Assert-WorkspacePath $staging) -Recurse -Force

# --- Remove old versions: now if free, else at the next reboot / sign-in -----------------------
function Mark-ForReboot([System.IO.FileSystemInfo]$item) {
    $null = Assert-WorkspacePath $item.FullName
    # A folder can only be removed at boot once it is empty: files first, then folders, deepest first.
    $paths = @()
    if ($item.PSIsContainer) {
        $paths += Get-ChildItem $item.FullName -Recurse -Force -File | ForEach-Object FullName
        $paths += Get-ChildItem $item.FullName -Recurse -Force -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object FullName
    }
    $paths += $item.FullName
    foreach ($p in $paths) { if (-not [Native.Kernel32]::MoveFileEx($p, $null, $DelayUntilReboot)) { return $false } }
    return $true
}

function Mark-ForSignIn([System.IO.FileSystemInfo]$item) {
    $cleanupPath = Assert-WorkspacePath $item.FullName
    $literal = $cleanupPath.Replace("'", "''")
    $rootLiteral = ($workspacePath + '\').Replace("'", "''")
    $cleanupScript = "`$cleanupTarget = [IO.Path]::GetFullPath('$literal'); if (`$cleanupTarget.StartsWith('$rootLiteral', [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath `$cleanupTarget -Recurse -Force -ErrorAction SilentlyContinue }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cleanupScript))
    $cmd = "powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded"
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
    $name = "Cleanup $(Split-Path $Target -Leaf) $($item.Name)"
    New-ItemProperty -Path $key -Name $name -Value $cmd -PropertyType String -Force | Out-Null
}

foreach ($item in Get-ChildItem -LiteralPath $Target -Force | Where-Object { $_.Name -match $oldPattern }) {
    try {
        $retiredPath = Assert-WorkspacePath $item.FullName
        Remove-Item -LiteralPath $retiredPath -Recurse -Force -ErrorAction Stop
        Write-Host "Deleted  $($item.Name)"
    }
    catch {
        if ($SkipScheduledCleanup) {
            Write-Host "In use   $($item.Name): kept for cleanup on the next publish"
            continue
        }
        if (Mark-ForReboot $item) { Write-Host "In use   $($item.Name): deleted at the next reboot" }
        else { Mark-ForSignIn $item; Write-Host "In use   $($item.Name): deleted at the next sign-in" }
    }
}

Write-Host "Done: $Target"
