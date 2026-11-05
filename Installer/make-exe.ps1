# Produce l'exe unico di Lemora (con .NET incluso: il PC di destinazione non deve averlo). E' anche il file da pubblicare:
# alla prima apertura chiede come installarsi (Portatile, Locale, Teams).
# Uso:  powershell -File Installer\make-exe.ps1 [-Copy O:\]
#   -Copy <cartella>  copia l'exe nella cartella indicata (per provarlo su un altro PC)
param([string]$Copy = '', [string]$Name = 'Lemora.exe')

$repo = (Resolve-Path "$PSScriptRoot\..").Path
$pub  = "$repo\ui-map\_publish_exe"

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish "$repo\Ui\Ui.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:SatelliteResourceLanguages=it -p:DebugType=none -o $pub --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish fallito' }

Get-ChildItem $pub | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
if (-not (Test-Path "$pub\Lemora.exe")) { throw "Lemora.exe non trovato in $pub" }

if ($Copy) {
    if (-not (Test-Path $Copy)) { throw "cartella di destinazione non raggiungibile: $Copy" }
    # se l'exe e' aperto altrove (es. su un altro PC) non si puo' sovrascrivere: l'errore deve fermare lo script, non passare inosservato
    Copy-Item "$pub\Lemora.exe" (Join-Path $Copy $Name) -Force -ErrorAction Stop
    $o = Get-Item (Join-Path $Copy $Name)
    "Copiato {0} ({1:0.0} MB, {2})" -f $o.FullName, ($o.Length / 1MB), $o.LastWriteTime
}
