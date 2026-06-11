# Builds a distributable .lplug4 package (a zip the Loupedeck/Logi software installs
# directly). Usage:  powershell -ExecutionPolicy Bypass -File package.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Prefer a system dotnet, fall back to the user-local install.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (& dotnet --list-sdks 2>$null)) {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

dotnet build "$root\DiscordSoundboardPlugin.sln" -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$out = Join-Path $root 'bin\Release'
$stage = Join-Path $root 'bin\package-stage'

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\bin", "$stage\metadata" | Out-Null

Copy-Item "$out\metadata\*" "$stage\metadata\"

# Ship the plugin and its real dependencies; the host provides PluginApi and friends.
$exclude = 'PluginApi.dll', 'PluginApi.xml'
Get-ChildItem "$out\bin" -File |
    Where-Object { $_.Name -notin $exclude -and $_.Extension -ne '.pdb' } |
    Copy-Item -Destination "$stage\bin\"

$package = Join-Path $root 'bin\DiscordSoundboard.lplug4'
Remove-Item $package -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Write entries by hand so paths use forward slashes per the zip spec
# (PowerShell 5.1's CreateFromDirectory stores backslashes).
$archive = [System.IO.Compression.ZipFile]::Open($package, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

Write-Host ""
Write-Host "Created: $package"
Write-Host "Install: double-click it, or use 'Install plugin' in the Loupedeck/Logi software."
