# Prepara i pacchetti del server Teams: un solo file per Windows e uno per Linux, senza .NET da installare.
# Uso: .\Server\publish-server.ps1 [-Out cartella]   (predefinito: _release\server)
param([string]$Out = (Join-Path $PSScriptRoot '..\_release\server'))
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'Server.csproj'
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

foreach ($rid in 'win-x64', 'linux-x64') {
    $tmp = Join-Path $Out "tmp-$rid"
    dotnet publish $proj -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $tmp --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish $rid fallito" }
    $exe = if ($rid -eq 'win-x64') { 'Lemora.Server.exe' } else { 'Lemora.Server' }
    $dest = Join-Path $Out "$rid"
    [IO.Directory]::CreateDirectory($dest) | Out-Null
    Copy-Item (Join-Path $tmp $exe) (Join-Path $dest $exe) -Force
    Copy-Item (Join-Path $PSScriptRoot 'LEGGIMI-SERVER.txt') $dest -Force
    [IO.Directory]::Delete($tmp, $true)
    $f = Get-Item (Join-Path $dest $exe)
    '{0}: {1} ({2:N1} MB)' -f $rid, $f.FullName, ($f.Length / 1MB)
}
