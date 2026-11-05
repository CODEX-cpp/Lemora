# Installa/aggiorna l'app "di produzione" in C:\Program Files\Lemora, compilata da questi sorgenti.
# I DATI NON SI TOCCANO: l'app installata tiene profilo e database in %APPDATA%\Lemora (Lemora.json, Lemora.db), fuori dalla cartella del programma.
# Registra l'installazione in Windows come fa l'installazione dall'app (voce in "App installate" e chiave HKLM\SOFTWARE\Lemora):
# senza, all'avvio l'app proporrebbe di nuovo la scelta "Come vuoi usare Lemora?".
# Serve l'amministratore (Program Files): se non lo e' si rilancia con la richiesta UAC.
# Uso:  powershell -File install-prod.ps1 [-NoBuild] [-CloseRunning] [-Mode local|teams]
#   -NoBuild       copia l'ultima compilazione in ui-map\_build_prod senza ricompilare
# La compilazione e' "ReadyToRun" (codice gia' tradotto per il processore): l'avvio e' circa 3 s piu' veloce che con la build normale.
#   -CloseRunning  chiude l'app installata se e' aperta (senza, lo script si ferma e lo dice)
#   -Mode          modalita' registrata (predefinita: quella gia' registrata, altrimenti local)
param([switch]$NoBuild, [switch]$CloseRunning, [string]$Mode = '')

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$src  = "$repo\ui-map\_build_prod"
$dst  = 'C:\Program Files\Lemora'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    # prima si compila (senza privilegi), poi si rilancia solo la copia con l'UAC
    if (-not $NoBuild) {
        Push-Location $repo
        if (Test-Path $src) { Remove-Item $src -Recurse -Force }   # cartella pulita: niente file avanzati da compilazioni vecchie
        dotnet publish Ui/Ui.csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o $src --nologo -v q
        if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'compilazione fallita' }
        Pop-Location
    }
    $args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-NoBuild')
    if ($CloseRunning) { $args2 += '-CloseRunning' }
    if ($Mode) { $args2 += @('-Mode', $Mode) }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $args2 -Wait -PassThru
    return
}

$running = @(Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dst\*" })
if ($running.Count -gt 0) {
    if (-not $CloseRunning) { Write-Host 'L''app installata e'' aperta: chiudila oppure rilancia con -CloseRunning.'; Read-Host 'Premi Invio'; return }
    $running | Stop-Process -Force; Start-Sleep 2
}
if (-not (Test-Path "$src\Lemora.exe")) { throw "compilazione non trovata in $src" }

# copia di sicurezza del database prima di ogni aggiornamento (si tengono le ultime 10)
$db = "$env:APPDATA\Lemora\Lemora.db"
if (Test-Path $db) {
    $bk = "$env:APPDATA\Lemora\backups"; New-Item -ItemType Directory -Force $bk | Out-Null
    Copy-Item $db "$bk\Lemora_$(Get-Date -Format 'yyyyMMdd-HHmm').db"
    Get-ChildItem $bk -Filter 'Lemora_*.db' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 10 | Remove-Item -Force
}
New-Item -ItemType Directory -Force $dst | Out-Null
# /MIR allinea la cartella; si escludono file di dati/profilo e i marcatori di modalita' (non devono mai essere cancellati o sovrascritti)
robocopy $src $dst /MIR /XF Lemora.db Lemora.json Lemora.dataSources.json FORCE_INTO_PORTABLE_MODE FORCE_INTO_APPDATA_MODE /XD .logs .locality .icons /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy ha fallito ($LASTEXITCODE)" }

# collegamento nel menu Start
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut("$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Lemora.lnk")
$s.TargetPath = "$dst\Lemora.exe"; $s.WorkingDirectory = $dst; $s.IconLocation = "$dst\Lemora.exe,0"; $s.Description = 'Lemora'; $s.Save()

# registrazione come l'installazione dall'app (SetupEngine): chiave dell'app e voce in "App installate"
$exe = "$dst\Lemora.exe"
$app = 'HKLM:\SOFTWARE\Lemora'
if (-not $Mode) { $Mode = (Get-ItemProperty $app -Name Mode -ErrorAction SilentlyContinue).Mode; if (-not $Mode) { $Mode = 'local' } }
$version = "$((Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', '') (build $((Get-Item $exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm')))"
New-Item -Path $app -Force | Out-Null
Set-ItemProperty $app -Name InstallLocation -Value $dst; Set-ItemProperty $app -Name Mode -Value $Mode; Set-ItemProperty $app -Name Version -Value $version
$un = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Lemora'
New-Item -Path $un -Force | Out-Null
$kb = [int](((Get-ChildItem $dst -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB)
@{ DisplayName = 'Lemora'; DisplayVersion = $version; Publisher = 'Lemora'; InstallLocation = $dst; DisplayIcon = $exe; UninstallString = "`"$exe`" --setup-uninstall" }.GetEnumerator() | ForEach-Object { Set-ItemProperty $un -Name $_.Key -Value $_.Value }
New-ItemProperty $un -Name EstimatedSize -Value $kb -PropertyType DWord -Force | Out-Null
New-ItemProperty $un -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty $un -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

# file .mora: si aprono con questa Lemora (per tutti gli utenti; se l'utente aveva gia' la sua associazione dal pulsante in Impostazioni, si aggiorna anche quella)
foreach ($root in 'HKLM:', 'HKCU:') {
    $cls = "$root\SOFTWARE\Classes"
    if ($root -eq 'HKCU:' -and -not (Test-Path "$cls\Lemora.Connection")) { continue }
    New-Item -Path "$cls\.mora" -Force | Out-Null; Set-ItemProperty "$cls\.mora" -Name '(default)' -Value 'Lemora.Connection'
    New-Item -Path "$cls\Lemora.Connection" -Force | Out-Null; Set-ItemProperty "$cls\Lemora.Connection" -Name '(default)' -Value 'Lemora'
    New-Item -Path "$cls\Lemora.Connection\DefaultIcon" -Force | Out-Null; Set-ItemProperty "$cls\Lemora.Connection\DefaultIcon" -Name '(default)' -Value "`"$exe`",0"
    New-Item -Path "$cls\Lemora.Connection\shell\open\command" -Force | Out-Null; Set-ItemProperty "$cls\Lemora.Connection\shell\open\command" -Name '(default)' -Value "`"$exe`" `"%1`""
}
Write-Host "Installato in $dst (modalita' $Mode, dati in $env:APPDATA\Lemora, non toccati)."
Start-Sleep 3
