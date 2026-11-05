# Screenshot della finestra principale con dati finti (demo "seed"), in istanza isolata e fuori schermo.
# Uso:  . ui-map\tools\restyle-shot.ps1 ; Shot-Main 'C:\percorso\out.png'
# Prima: dotnet build ... -o ui-map\_build_new ; robocopy ui-map\_build_new ui-map\_build /MIR
. (Join-Path $PSScriptRoot 'reach-main.ps1')

function Shot-Main([string]$Out, [string]$Slug = 'restyle', [string]$Demo = 'seed', [int]$Wait = 4) {
    $first = Start-Main $Slug          # crea i dati (wizard) poi chiude
    Stop-Instance $first
    Start-Sleep -Seconds 2
    $run = Join-Path $script:RunRoot $Slug
    $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $run 'Lemora.exe')
    $psi.WorkingDirectory = $run
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['LEMORA_INSTANCE_ID'] = $Slug
    $psi.EnvironmentVariables['LEMORA_HEADLESS'] = '1'
    $psi.EnvironmentVariables['LEMORA_DEMO'] = $Demo
    $script:UserFg = [UiaWin]::GetForegroundWindow()
    $p = [Diagnostics.Process]::Start($psi)
    $m = Wait-Until { MainWin $p.Id } 60
    Move-Offscreen $p.Id
    Start-Sleep -Seconds $Wait
    Move-Offscreen $p.Id
    Restore-Foreground
    $m = MainWin $p.Id
    Save-Screenshot $m $Out | Out-Null
    return $p.Id
}

# Avvia l'istanza gia' popolata (dati persistenti dopo Shot-Main) e fa lo screenshot dopo $Wait secondi.
function Shot-Existing([string]$Out, [string]$Slug = 'restyle', [int]$Wait = 6, [string]$Demo = '') {
    $run = Join-Path $script:RunRoot $Slug
    $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $run 'Lemora.exe')
    $psi.WorkingDirectory = $run
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['LEMORA_INSTANCE_ID'] = $Slug
    $psi.EnvironmentVariables['LEMORA_HEADLESS'] = '1'
    if ($Demo) { $psi.EnvironmentVariables['LEMORA_DEMO'] = $Demo }
    $script:UserFg = [UiaWin]::GetForegroundWindow()
    $p = [Diagnostics.Process]::Start($psi)
    $null = Wait-Until { MainWin $p.Id } 60
    Move-Offscreen $p.Id; Start-Sleep $Wait; Move-Offscreen $p.Id; Restore-Foreground
    Save-Screenshot (MainWin $p.Id) $Out | Out-Null
    return $p.Id
}

# Tutto in uno: crea i dati finti (demo seed), poi rilancia e fotografa. Chiude l'istanza.
function Shot-Seeded([string]$Out, [string]$Slug = 'restyle') {
    $a = [int](Shot-Main (Join-Path $env:TEMP 'seed-first.png') $Slug 'seed' 8 | Select-Object -Last 1)
    Stop-Instance $a
    Start-Sleep 2
    $b = [int](Shot-Existing $Out $Slug | Select-Object -Last 1)
    Stop-Instance $b
}

# Screenshot disegnato da WPF dentro l'app (LEMORA_SHOT): mostra anche gli aggiornamenti dopo il primo disegno.
# $Demo: nome demo (detail, seed, none...). $Extra: variabili d'ambiente aggiuntive (hashtable).
function Shot-App([string]$Out, [string]$Slug = 'restyle', [string]$Demo = 'detail', [int]$DelayMs = 4000, [hashtable]$Extra = @{}) {
    $run = Join-Path $script:RunRoot $Slug
    if (Test-Path $Out) { Remove-Item $Out -Force }
    $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $run 'Lemora.exe')
    $psi.WorkingDirectory = $run
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['LEMORA_INSTANCE_ID'] = $Slug
    $psi.EnvironmentVariables['LEMORA_HEADLESS'] = '1'
    $psi.EnvironmentVariables['LEMORA_DEMO'] = $Demo
    $psi.EnvironmentVariables['LEMORA_SHOT'] = $Out
    $psi.EnvironmentVariables['LEMORA_SHOT_DELAY'] = "$DelayMs"
    foreach ($k in $Extra.Keys) { $psi.EnvironmentVariables[$k] = [string]$Extra[$k] }
    $script:UserFg = [UiaWin]::GetForegroundWindow()
    $p = [Diagnostics.Process]::Start($psi)
    $null = Wait-Until { MainWin $p.Id } 60
    Move-Offscreen $p.Id
    $null = Wait-Until { Test-Path $Out } 60
    Start-Sleep -Milliseconds 400
    Restore-Foreground
    Stop-Instance $p.Id
    if (Test-Path $Out) { "screenshot: $Out" } else { "screenshot NON creato" }
}
