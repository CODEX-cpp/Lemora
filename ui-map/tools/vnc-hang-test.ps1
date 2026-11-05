# Prova se il client VNC integrato blocca l'interfaccia con vari server finti (127.0.0.1:5901 normale, 5902 password, 5903 tipi proprietari, 5904 muto).
# Uso:  . ui-map\tools\vnc-hang-test.ps1 [-Ports 5902,5903]
param([int[]]$Ports = @(5901, 5902, 5903, 5904))
$run = 'C:\_dev\_projects\_repos\1Remote\ui-map\_run\shot3'
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\shot3\*' } | Stop-Process -Force
robocopy 'C:\_dev\_projects\_repos\1Remote\ui-map\_build_new' $run /E /XF Lemora.db Lemora.json /NFL /NDL /NJH /NJS /NP | Out-Null
foreach ($port in $Ports) {
    $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $run 'Lemora.exe')
    $psi.WorkingDirectory = $run
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['LEMORA_INSTANCE_ID'] = 'shot3'
    $psi.EnvironmentVariables['LEMORA_HEADLESS'] = '1'
    $psi.EnvironmentVariables['LEMORA_DEMO'] = "quick-to:127.0.0.1:$port"
    $p = [Diagnostics.Process]::Start($psi)
    $notResp = 0; $total = 0
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        $p.Refresh()
        if ($p.HasExited) { break }
        $total++
        if (-not $p.Responding) { $notResp++ }
    }
    $state = if ($p.HasExited) { "chiusa da sola (exit $($p.ExitCode))" } elseif ($notResp -ge 6) { "BLOCCATA ($notResp/$total controlli senza risposta)" } else { "risponde ($notResp/$total senza risposta)" }
    "porta ${port}: $state"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}
