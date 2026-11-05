# Avvia visibile la copia di prova (ui-map\_run\restyle) con l'ultima build di ui-map\_build_new, senza toccare la produzione.
# Ferma solo l'istanza che gira da _run\restyle. Niente scorciatoia globale, per non rubare quella dell'app installata.
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$run  = "$repo\ui-map\_run\restyle"
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\restyle\*' } | Stop-Process -Force
Start-Sleep 1
robocopy "$repo\ui-map\_build_new" $run /E /XF Lemora.db Lemora.json /NFL /NDL /NJH /NJS /NP | Out-Null
$env:LEMORA_NO_HOTKEY = '1'
Start-Process "$run\Lemora.exe" -WorkingDirectory $run
"Copia di prova avviata da $run"
