# Scheda "+": apre un'altra Home; la X la chiude
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
$P = Start-Main 'uitest'
Move-Offscreen $P
function W { Main-Win $P }
function Tabs { @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' }) }
function Tab-Title($t) { (Find-Element $t -Type Text | ForEach-Object { $_.Current.Name }) -join '' }
Step 'plus' {
    $t0 = Tabs
    Check 'Schede iniziali: Home + "+"' ($t0.Count -eq 2) ("n=" + $t0.Count + ": " + (($t0 | ForEach-Object { Tab-Title $_ }) -join ' | '))
    $plus = @($t0 | Where-Object { (Tab-Title $_) -eq '+' })[0]
    Check 'Scheda "+" presente' ([bool]$plus)
    Do-Invoke (Find-Element $plus -Type Button)[0] | Out-Null; Start-Sleep -Milliseconds 900
    $t1 = Tabs
    $titles = @($t1 | ForEach-Object { Tab-Title $_ })
    Check '"+" aggiunge una seconda scheda Home' (($titles | Where-Object { $_ -eq 'Home' }).Count -eq 2) ($titles -join ' | ')
    Check 'Nessun pannello Avvio rapido aperto' (-not (@(Get-Windows $P | Where-Object { $_.Current.Name -eq 'Launcher' }).Count -gt 0 -and (Has-Text (W) 'Alt + M')))
    Check 'La Home e visibile' (Has-Text (W) 'Nuova cartella')
    # seconda "+" -> terza Home
    $plus = @($t1 | Where-Object { (Tab-Title $_) -eq '+' })[0]
    Do-Invoke (Find-Element $plus -Type Button)[0] | Out-Null; Start-Sleep -Milliseconds 700
    Check 'Un altro "+": tre schede Home' ((@(Tabs | ForEach-Object { Tab-Title $_ }) | Where-Object { $_ -eq 'Home' }).Count -eq 3)
    # chiudo l'ultima Home (la X e' l'ultimo bottone della scheda)
    $homes = @(Tabs | Where-Object { (Tab-Title $_) -eq 'Home' })
    $x = @(Find-Element $homes[2] -Type Button)[-1]
    Do-Invoke $x | Out-Null; Start-Sleep -Milliseconds 800
    Check 'La X chiude una scheda Home' ((@(Tabs | ForEach-Object { Tab-Title $_ }) | Where-Object { $_ -eq 'Home' }).Count -eq 2)
    Check-NoError $P 'scheda +'
}
Write-Host ""
Write-Host ("RIEPILOGO: {0} OK, {1} FALLITI" -f @($script:Results | Where-Object Esito -eq 'OK').Count, @($script:Results | Where-Object Esito -eq 'FALLITO').Count)
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
