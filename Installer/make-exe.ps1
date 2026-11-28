# Builds the single Lemora exe (with .NET included: the target PC does not need it). It is also the file to publish:
# on first open it asks how to install itself (Portable, Local, Teams).
# Usage:  powershell -File Installer\make-exe.ps1 [-Copy D:\Share]
#   -Copy <folder>  copies the exe to the given folder (to try it on another PC)
#   -Name           name of the copy; default Lemora_Installer_<version>.exe (setup installs it as Lemora.exe anyway)
param([string]$Copy = '', [string]$Name = '')

$repo = (Resolve-Path "$PSScriptRoot\..").Path
$pub  = "$repo\ui-map\_publish_exe"

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish "$repo\Ui\Ui.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:SatelliteResourceLanguages=it -p:DebugType=none -o $pub --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Get-ChildItem $pub | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
if (-not (Test-Path "$pub\Lemora.exe")) { throw "Lemora.exe not found in $pub" }

if ($Copy) {
    if (-not (Test-Path $Copy)) { throw "destination folder not reachable: $Copy" }
    if (-not $Name) { $Name = "Lemora_Installer_$((Get-Item "$pub\Lemora.exe").VersionInfo.FileVersion).exe" }
    # if the exe is open elsewhere (e.g. on another PC) it cannot be overwritten: the error must stop the script, not go unnoticed
    Copy-Item "$pub\Lemora.exe" (Join-Path $Copy $Name) -Force -ErrorAction Stop
    $o = Get-Item (Join-Path $Copy $Name)
    "Copied {0} ({1:0.0} MB, {2})" -f $o.FullName, ($o.Length / 1MB), $o.LastWriteTime
}
