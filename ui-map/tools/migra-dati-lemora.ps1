# Passaggio dei dati dal vecchio nome tecnico (1Remote) al nuovo (Lemora), in due tempi:
#   -Esporta : copia %APPDATA%\1Remote e %LOCALAPPDATA%\1Remote in Documenti\Lemora-migrazione\<data> (prima di disinstallare)
#   -Importa : dalla copia piu' recente (o da -Da <cartella>) crea %APPDATA%\Lemora con Lemora.db e Lemora.json (dopo aver installato la nuova versione)
# L'app deve essere chiusa: il database aperto non si copia in modo sicuro.
param([switch]$Esporta, [switch]$Importa, [string]$Da)
$ErrorActionPreference = 'Stop'
$base = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Lemora-migrazione'
$roam = [Environment]::GetFolderPath('ApplicationData')
$local = [Environment]::GetFolderPath('LocalApplicationData')

if (Get-Process Lemora, 1Remote -ErrorAction SilentlyContinue) { 'Lemora e'' aperta: chiudila (anche dalla tray) e rilancia lo script.'; exit 1 }

if ($Esporta) {
    $src = Join-Path $roam '1Remote'
    if (-not (Test-Path "$src\1Remote.db")) { "Non trovo $src\1Remote.db: niente da esportare."; exit 1 }
    $dst = Join-Path $base (Get-Date -Format 'yyyyMMdd-HHmmss')
    New-Item -ItemType Directory -Force "$dst\Roaming", "$dst\Local" | Out-Null
    Copy-Item "$src\*" "$dst\Roaming" -Recurse
    if (Test-Path "$local\1Remote\.icons") { Copy-Item "$local\1Remote\.icons" "$dst\Local" -Recurse }
    $a = (Get-FileHash "$src\1Remote.db").Hash; $b = (Get-FileHash "$dst\Roaming\1Remote.db").Hash
    "Esportato in $dst"
    "  database: $([math]::Round((Get-Item "$dst\Roaming\1Remote.db").Length / 1KB)) KB, copia identica all'originale: $($a -eq $b)"
    exit 0
}

if ($Importa) {
    if (-not $Da) { $Da = Get-ChildItem $base -Directory -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName }
    if (-not $Da -or -not (Test-Path "$Da\Roaming\1Remote.db")) { "Nessuna esportazione trovata in $base (prima: -Esporta)."; exit 1 }
    $dst = Join-Path $roam 'Lemora'
    if (Test-Path "$dst\Lemora.db") {
        # il primo avvio della nuova versione crea un database vuoto: si mette da parte, non si cancella
        $keep = "$dst-prima-dell-importazione-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Move-Item $dst $keep
        "Cartella Lemora gia' esistente spostata in $keep"
    }
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item "$Da\Roaming\1Remote.db" "$dst\Lemora.db"
    # profilo: stesso contenuto, solo il percorso del database cambia
    $json = [IO.File]::ReadAllText("$Da\Roaming\1Remote.json")
    $old = (Join-Path $roam '1Remote\1Remote.db').Replace('\', '\\')
    $new = (Join-Path $roam 'Lemora\Lemora.db').Replace('\', '\\')
    $json = $json.Replace($old, $new)
    [IO.File]::WriteAllText("$dst\Lemora.json", $json, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path "$Da\Roaming\1Remote.dataSources.json") { Copy-Item "$Da\Roaming\1Remote.dataSources.json" "$dst\Lemora.dataSources.json" }
    if (Test-Path "$Da\Roaming\backups") {
        New-Item -ItemType Directory -Force "$dst\backups" | Out-Null
        Get-ChildItem "$Da\Roaming\backups" -File | ForEach-Object { Copy-Item $_.FullName (Join-Path "$dst\backups" ($_.Name -replace '^1Remote', 'Lemora')) }
    }
    if (Test-Path "$Da\Local\.icons") { New-Item -ItemType Directory -Force "$local\Lemora" | Out-Null; Copy-Item "$Da\Local\.icons" "$local\Lemora" -Recurse -Force }
    $a = (Get-FileHash "$Da\Roaming\1Remote.db").Hash; $b = (Get-FileHash "$dst\Lemora.db").Hash
    "Importato da $Da in $dst"
    "  database identico all'esportazione: $($a -eq $b)"
    "  percorso del database nel profilo: " + ((Select-String -Path "$dst\Lemora.json" -Pattern 'SqliteDatabasePath').Line.Trim())
    'La vecchia cartella %APPDATA%\1Remote non e'' stata toccata: si cancella a mano quando Lemora funziona.'
    exit 0
}
'Uso: migra-dati-lemora.ps1 -Esporta   (prima di disinstallare)   oppure   -Importa [-Da <cartella>]   (dopo aver installato)'
