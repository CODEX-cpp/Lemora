# Prova nascosta della fluidita' del ridimensionamento: la demo "resize-corners" trascina via codice i quattro angoli della finestra
# principale (fuori schermo) e scrive nel log quanto dura ogni passo. Non tocca schermo, mouse, tastiera. Usa l'ultima build in ui-map\_build_new.
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$run = "$root\ui-map\_run\restyle"
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\restyle\*' } | Stop-Process -Force
Start-Sleep 1
robocopy "$root\ui-map\_build_new" $run /E /XF Lemora.db Lemora.json /NFL /NDL /NJH /NJS /NP | Out-Null
$env:LEMORA_INSTANCE_ID = 'restyle'; $env:LEMORA_HEADLESS = '1'; $env:LEMORA_NO_HOTKEY = '1'; $env:LEMORA_DEMO = 'resize-corners'
$t0 = Get-Date
$p = Start-Process "$run\Lemora.exe" -WorkingDirectory $run -WindowStyle Hidden -PassThru
Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_NO_HOTKEY, Env:LEMORA_DEMO
# niente lettura del registro mentre l'app gira: si aspetta un tempo fisso
Start-Sleep 45
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
$log = Get-ChildItem "$run\.logs" -Filter 'Lemora.log_*.md' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $log.FullName | Select-String 'DEMO resize' | Where-Object { $_.Line -match '^\[T:\d+\]\[([\d:.]+)\]' -and [datetime]::ParseExact($Matches[1], 'HH:mm:ss.fff', $null) -ge $t0.AddSeconds(-1) } | ForEach-Object { $_.Line -replace '^.*\]\s+', '' }
