# Screenshot senza UI Automation (che a volte non risponde): l'app scatta da sola (LEMORA_SHOT) e qui si aspetta il file.
# Uso:  . ui-map\tools\shot-noui.ps1 ; Shot-NoUi 'out.png' 'shot3' 'add,editor-ssh' 6000
# Non usare robocopy /MIR su _run\<slug>: cancella i dati dell'istanza (si ricade nel wizard iniziale).
function Shot-NoUi([string]$Out, [string]$Slug = 'shot3', [string]$Demo = 'add', [int]$DelayMs = 5000, [int]$TimeoutSec = 60, [string]$Window = '') {
    $run = Join-Path (Join-Path $PSScriptRoot '..\_run') $Slug
    if (Test-Path $Out) { Remove-Item $Out -Force }
    $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $run 'Lemora.exe')
    $psi.WorkingDirectory = $run
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['LEMORA_INSTANCE_ID'] = $Slug
    $psi.EnvironmentVariables['LEMORA_HEADLESS'] = '1'
    $psi.EnvironmentVariables['LEMORA_DEMO'] = $Demo
    $psi.EnvironmentVariables['LEMORA_SHOT'] = $Out
    if ($Window) { $psi.EnvironmentVariables['LEMORA_SHOT_WINDOW'] = $Window }
    $psi.EnvironmentVariables['LEMORA_SHOT_DELAY'] = "$DelayMs"
    $p = [Diagnostics.Process]::Start($psi)
    $t = 0
    while (-not (Test-Path $Out) -and $t -lt $TimeoutSec) { Start-Sleep -Milliseconds 500; $t += 0.5 }
    Start-Sleep -Milliseconds 400
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    if (Test-Path $Out) { "screenshot: $Out" } else { "screenshot NON creato" }
}
