# Impostazioni > Aspetto > Font family: elenco con gruppo "Piu usati" e nomi nel proprio carattere
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
$P = Start-Main 'uitest'
Move-Offscreen $P
function W { Main-Win $P }
Step 'apri' {
    Do-Invoke (Up-To (Texts (W) 'Opzioni')[0] 'Invoke') | Out-Null; Start-Sleep -Milliseconds 1200
    Do-Invoke (Up-To (Texts (W) 'Aspetto')[0] 'Invoke') | Out-Null; Start-Sleep -Milliseconds 1000
    Check 'Aspetto: riga Font family' (Has-Text (W) 'Font family')
    $cb = After-Text (W) 'Font family' 'ComboBox'
    Check 'Aspetto: casella dei font trovata' ($cb -ne $null)
    $ec = $cb.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 700
    $names = @(Find-Element (W) -Type ListItem | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
    $texts = @(Find-Element (W) -Type Text | ForEach-Object { $_.Current.Name })
    Write-Host ("  voci (prime 40): " + (($names | Select-Object -First 40) -join ' | '))
    # le intestazioni di gruppo non compaiono nell'albero UIA: si controllano nello screenshot
    foreach ($f in 'Arial', 'Calibri', 'Segoe UI', 'Verdana', 'Georgia', 'Times New Roman', 'Tahoma', 'Trebuchet MS', 'Cambria', 'Candara') {
        Check "Font classico nell'elenco: $f" (($texts -contains $f) -or ($names -match [regex]::Escape($f)))
    }
    $o = Join-Path $script:ShotDir 'ui_font-elenco.png'
    Save-ScreenshotWithPopups (W) $o @() | Out-Null
    Write-Host "  screenshot: $o"
    try { $ec.Collapse() } catch {}
}
Write-Host ""
Write-Host ("RIEPILOGO: {0} OK, {1} FALLITI" -f @($script:Results | Where-Object Esito -eq 'OK').Count, @($script:Results | Where-Object Esito -eq 'FALLITO').Count)
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
