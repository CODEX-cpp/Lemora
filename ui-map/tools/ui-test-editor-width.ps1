# Editor RDP: accordion "Schermo" espanso, per vedere se i testi lunghi entrano (screenshot interno a ritardo fisso)
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
$env:LEMORA_DEMO = 'noop'; $env:LEMORA_SHOT = 'C:\_dev\_projects\_repos\1Remote\ui-map\_shots\editor_rdp.png'; $env:LEMORA_SHOT_DELAY = '22000'
Remove-Item $env:LEMORA_SHOT -ErrorAction SilentlyContinue
$P = Start-Main 'uitest'
Move-Offscreen $P
function W { Main-Win $P }
Step 'editor' {
    $c = (Find-Element (W) -Type Custom)[0]
    $add = @($c.FindAll('Children', $script:TrueCond) | ForEach-Object { $_ } | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' })[0]
    Do-Invoke $add | Out-Null
    Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 } | Out-Null
    $lb = (Find-Element (W) -Id 'ListBoxProtocols')[0]
    Do-Select (@(Find-Element $lb -Type ListItem | Where-Object { (Find-Element $_ -Name 'RDP' -Type Text).Count -gt 0 })[0]) | Out-Null
    Start-Sleep -Milliseconds 1200
    foreach ($name in 'Schermo', 'Impostazioni avanzate') {
        $g = @(Find-Element (W) -Name $name -Type Group)[0]
        if ($g) { try { $g.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch {} ; Check "Accordion '$name' espanso" $true } else { Check "Accordion '$name' trovato" $false }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 25
}
Write-Host ("RIEPILOGO: {0} OK, {1} FALLITI" -f @($script:Results | Where-Object Esito -eq 'OK').Count, @($script:Results | Where-Object Esito -eq 'FALLITO').Count)
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
