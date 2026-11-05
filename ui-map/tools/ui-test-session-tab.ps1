# Sessione dalla Home come scheda in alto (server finto su 127.0.0.1:5901: python ui-map\tools\fake-servers\vnc_server.py)
param([string]$Proto = 'VNC', [int]$Port = 5901, [string]$SessName = 'Sessione-Prova')
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
$P = Start-Main 'uitest'
Move-Offscreen $P
function W { Main-Win $P }
function In-Win($w, $e) { $r = $e.Current.BoundingRectangle; $wr = $w.Current.BoundingRectangle; $r.Y -ge $wr.Y -and $r.Bottom -le ($wr.Y + $wr.Height) -and $r.X -ge $wr.X }
function Tab-Names { @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' -and (Find-Element $_ -Name '+' -Type Text).Count -eq 0 } | ForEach-Object { (Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '' }) }

Write-Host "=== SESSIONE VNC COME SCHEDA ==="
Step 'crea' {
    $c = (Find-Element (W) -Type Custom)[0]
    $add = @($c.FindAll('Children', $script:TrueCond) | ForEach-Object { $_ } | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' })[0]
    Do-Invoke $add | Out-Null
    Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 } | Out-Null
    $lb = (Find-Element (W) -Id 'ListBoxProtocols')[0]
    Do-Select (@(Find-Element $lb -Type ListItem | Where-Object { (Find-Element $_ -Name $Proto -Type Text).Count -gt 0 })[0]) | Out-Null
    Start-Sleep -Milliseconds 900
    Do-SetValue (After-Text (W) 'Nome' 'Edit') $SessName | Out-Null
    Do-SetValue (After-Text (W) 'Nome host' 'Edit') '127.0.0.1' | Out-Null
    Start-Sleep -Milliseconds 500
    # porta 5901: il campo dopo "Porta"
    Do-SetValue (After-Text (W) 'Porta' 'Edit') "$Port" | Out-Null
    $pw = @(Find-Element (W) -Id 'CipherTextBox'); if ($pw.Count) { Do-SetValue $pw[0] 'x' | Out-Null }
    Start-Sleep -Milliseconds 500
    Do-Invoke (Find-Element (W) -Id 'ButtonSave')[0] | Out-Null
    Check 'Server VNC creato' ([bool](Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -eq 0 } 10))
}
Step 'connetti' {
    Do-SetValue (Find-Element (W) -Id 'TbMainFilter')[0] $SessName | Out-Null; Start-Sleep -Milliseconds 900
    Do-SetValue (Find-Element (W) -Id 'TbMainFilter')[0] '' | Out-Null; Start-Sleep -Milliseconds 900; Write-Host ('  tutte le voci: ' + ((@(Find-Element (W) -Id 'LvServerCards') | ForEach-Object { Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name } }) -join ' | ')); Do-SetValue (Find-Element (W) -Id 'TbMainFilter')[0] $SessName | Out-Null; Start-Sleep -Milliseconds 900; Write-Host ('  filtro/voci: ' + @(Find-Element (Find-Element (W) -Id 'LvServerCards')[0] -Type ListItem).Count + ' ; testi: ' + ((Find-Element (W) -Type Text | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like '*Prova*' -or $_ -like '127*' }) -join ' | '))
    $item = @(Find-Element (Find-Element (W) -Id 'LvServerCards')[0] -Type ListItem)[0]
    Do-Select $item | Out-Null; Start-Sleep -Milliseconds 800
    $w = W
    $t = @(Texts $w 'Connetti' | Where-Object { In-Win $w $_ })
    Do-Invoke (Up-To $t[$t.Count - 1] 'Invoke') | Out-Null
    # il VNC senza password salvata la chiede: si compila il riquadro e si preme OK
    $dlg = $null; for ($i = 0; $i -lt 20 -and -not $dlg; $i++) { Start-Sleep -Milliseconds 400; $dlg = @(Find-Element (W) -Type Window | Where-Object { (Find-Element $_ -Id 'CipherTextBox').Count -gt 0 })[0] }
    if ($dlg) {
        Do-SetValue (Find-Element $dlg -Id 'CipherTextBox')[0] 'x' | Out-Null; Start-Sleep -Milliseconds 400
        $okb = @(Find-Element $dlg -Type Button | Where-Object { ((Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '') -eq 'OK' })[0]
        Do-Invoke $okb | Out-Null
    }
    $ok = Wait-Cond { (Tab-Names) -contains $SessName } 15
    Write-Host ('  finestre figlie: ' + ((@(Find-Element (W) -Type Window) | ForEach-Object { $_.Current.Name }) -join ' | '))
    Write-Host ('  testi dialogo: ' + ((@(Find-Element (W) -Type Window | Select-Object -First 1 | ForEach-Object { Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name } })) -join ' / '))
    Check 'Connetti: compare la scheda della sessione' ([bool]$ok) ("schede: " + ((Tab-Names) -join ' | '))
    Start-Sleep -Milliseconds 1500
    $tabs = @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' -and (Find-Element $_ -Name '+' -Type Text).Count -eq 0 })
    $foc = @(Find-Element (W) | Where-Object { $_.Current.HasKeyboardFocus })
    Write-Host ('  elemento con il focus: ' + (($foc | ForEach-Object { $_.Current.ControlType.ProgrammaticName + '/' + $_.Current.ClassName + '/' + $_.Current.Name }) -join ' ; '))
    Check 'Schede: Home + sessione' ($tabs.Count -eq 2)
    Check 'Nessuna finestra di sessione separata' (@(Get-Windows $P | Where-Object { $_.Current.ClassName -eq 'Window' }).Count -eq 1) ("finestre: " + (@(Get-Windows $P) | ForEach-Object { $_.Current.Name }) -join ' | ')
    Check-NoError $P 'Connetti'
    Shot $P 'sessione-scheda'
}
Step 'torna-home' {
    $tabs = @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' -and (Find-Element $_ -Name '+' -Type Text).Count -eq 0 })
    Do-Invoke (Find-Element $tabs[0] -Type Button)[0] | Out-Null
    Start-Sleep -Milliseconds 800
    Check 'Home: torna alla lista' (Has-Text (W) 'Nuova cartella')
    $tabs = @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' -and (Find-Element $_ -Name '+' -Type Text).Count -eq 0 })
    Do-Invoke (Find-Element $tabs[1] -Type Button)[0] | Out-Null
    Start-Sleep -Milliseconds 800
    Check 'Scheda sessione: si rimostra' ((Tab-Names) -contains $SessName)
}
Step 'chiudi' {
    $tabs = @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' -and (Find-Element $_ -Name '+' -Type Text).Count -eq 0 })
    $close = @(Find-Element $tabs[1] -Type Button)[-1]
    Do-Invoke $close | Out-Null
    Start-Sleep -Milliseconds 1500
    # eventuale conferma
    $dlg = @(Find-Element (W) -Type Window)[0]
    if ($dlg) {
        $yes = @(Find-Element $dlg -Type Button | Where-Object { ((Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '') -match '^S' })[0]
        if ($yes) { Do-Invoke $yes | Out-Null; Start-Sleep -Milliseconds 1500 }
    }
    Check 'Chiudi (X): la scheda sparisce' (-not ((Tab-Names) -contains $SessName)) ("schede: " + ((Tab-Names) -join ' | '))
    Check-NoError $P 'chiusura sessione'
}
Write-Host ""
Write-Host ("RIEPILOGO: {0} OK, {1} FALLITI" -f @($script:Results | Where-Object Esito -eq 'OK').Count, @($script:Results | Where-Object Esito -eq 'FALLITO').Count)
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force










