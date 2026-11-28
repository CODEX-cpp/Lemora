# Prepares the files of a Lemora release for GitHub, in ui-map\_release\<version>\ (publishes nothing):
#   Lemora.exe                               the single exe: it is also the installer for a fresh start (attach it to the release)
#   lemora-<version>-update-package.zip      what already installed apps download (Lemora.exe at the root of the zip)
#   lemora-<version>-update-package.zip.sig  the signature (needs this PC's private key: ~\.lemora-updater.key, see new-keys.ps1)
# Then on GitHub (CODEX-cpp/Lemora > Releases > Draft a new release): tag "v<version>", and attach the three files.
# The tag MUST be v<version> with the version printed below: installed apps compare exactly that.
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $SkipBuild) { & "$repo\Installer\make-exe.ps1" }
$exe = "$repo\ui-map\_publish_exe\Lemora.exe"
if (-not (Test-Path $exe)) { throw "Missing ${exe}: run without -SkipBuild" }
$ver = (Get-Item $exe).VersionInfo.FileVersion
if ($ver -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Invalid exe version: '$ver'" }
$out = "$repo\ui-map\_release\$ver"
if (Test-Path $out) { [IO.Directory]::Delete($out, $true) }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $exe "$out\Lemora.exe"
$zip = "$out\lemora-$ver-update-package.zip"
Compress-Archive -Path $exe -DestinationPath $zip
& "$PSScriptRoot\sign.ps1" -File $zip
"`nRelease $ver ready in $out"
Get-ChildItem $out | ForEach-Object { '{0,8:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name }
"Tag to use on GitHub: v$ver"
