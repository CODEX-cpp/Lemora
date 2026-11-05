# Ferma l'app di sviluppo (solo quella in _run\restyle), copia l'ultima build di Ui\bin\Debug e la avvia visibile, senza scorciatoia globale.
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$run  = "$repo\ui-map\_run\restyle"
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\restyle\*' } | Stop-Process -Force
Start-Sleep 1
robocopy "$repo\Ui\bin\Debug\net10.0-windows10.0.19041.0" $run /E /NFL /NDL /NJH /NJS /NP | Out-Null
# niente LEMORA_INSTANCE_ID: la copia si isola da sola ('portable-<hash del percorso>'), con la stessa identita' che ottiene
# quando Windows la rilancia (tasto destro sulla barra, collegamento, doppio clic). Con un ID diverso il rilancio apriva una seconda istanza.
$env:LEMORA_NO_HOTKEY = '1'
Start-Process "$run\Lemora.exe" -WorkingDirectory $run
Start-Sleep 20
& "$PSScriptRoot\show-app.ps1"
