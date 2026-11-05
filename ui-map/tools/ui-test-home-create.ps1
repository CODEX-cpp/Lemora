# Test reali: Home e creazione oggetto. Uso:  . ui-map\tools\ui-test-home-create.ps1   (usa l'istanza 'uitest', creandola da zero)
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
$P = Start-Main 'uitest'
Move-Offscreen $P
function W { Main-Win $P }
Write-Host "=== HOME ==="
Step 'Home: tab Home presente' { Check 'Home: tab Home presente' (Has-Text (W) 'Home') }
Step 'Home: schermata iniziale' { $w = W; Check 'Home: sezioni PREFERITI e CRONOLOGIA' ((Has-Text $w 'PREFERITI') -and (Has-Text $w 'CRONOLOGIA')) }
Step 'Home: casella ricerca' {
    $tb = (Find-Element (W) -Id 'TbMainFilter')[0]
    Do-SetValue $tb '10.0.0.5' | Out-Null
    Start-Sleep -Milliseconds 600
    $v = $tb.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    Check 'Home: testo digitato nella ricerca' ($v -eq '10.0.0.5') $v
    Check 'Home: selettore tipo connessione presente' ((Find-Element (W) -Id 'CbQuickProtocol').Count -gt 0)
}
Shot $P 'home-ricerca'

Write-Host "=== CREAZIONE ==="
$addBtn = $null
Step 'Aggiungi: apre editor' {
    $c = (Find-Element (W) -Type Custom)[0]
    $script:addBtn = @($c.FindAll('Children', $script:TrueCond) | ForEach-Object { $_ } | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' })[0]
    Do-Invoke $script:addBtn | Out-Null
    $ok = Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 }
    Check 'Aggiungi: editor aperto' ([bool]$ok)
}
Shot $P 'editor-aperto'
Step 'Editor: indirizzo precompilato' {
    $w = W
    $edits = @(Find-Element $w -Type Edit | Where-Object { $_.Current.IsEnabled })
    $vals = $edits | ForEach-Object { try { $_.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' } }
    Check 'Editor: IP della ricerca nell''indirizzo' ($vals -contains '10.0.0.5') ($vals -join ' | ')
    Check 'Editor: tab "Nuova connessione" nella barra' ((Texts $w 'Nuova connessione').Count -ge 2) ("occorrenze: " + (Texts $w 'Nuova connessione').Count)
}
Step 'Editor: cambio protocollo SSH' {
    $w = W
    $lb = (Find-Element $w -Id 'ListBoxProtocols')[0]
    $sshItem = @(Find-Element $lb -Type ListItem | Where-Object { (Find-Element $_ -Name 'SSH' -Type Text).Count -gt 0 })[0]
    Do-Select $sshItem | Out-Null
    $ok = Wait-Cond { Has-Text (W) 'Shell sicura' -or (Find-Element (W) -Name 'UTENTI' -Type Text).Count -gt 0 }
    Start-Sleep -Milliseconds 700
    $w = W
    Check 'Editor: form SSH mostrato (sezione UTENTI)' ((Has-Text $w 'UTENTI'))
    $edits = @(Find-Element $w -Type Edit)
    $vals = $edits | ForEach-Object { try { $_.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' } }
    Check 'Editor: porta SSH 22' ($vals -contains '22') ($vals -join ' | ')
}
Step 'Editor: nome e riepilogo vivo' {
    $w = W
    $name = After-Text $w 'Nome' 'Edit'
    Do-SetValue $name 'UI-Test-SSH' | Out-Null
    Start-Sleep -Milliseconds 700
    Check 'Editor: riepilogo mostra il nome digitato' (Has-Text (W) 'UI-Test-SSH')
}
Step 'Editor: cartella (menu)' {
    $w = W
    $combo = After-Text $w 'Cartella' 'ComboBox'
    Check 'Editor: elenco cartelle presente' ($combo -ne $null)
}
Step 'Editor: utente locale' {
    $w = W
    Click-Text $w '+ Aggiungi utente'
    Start-Sleep -Milliseconds 600
    $w = W
    $userEdit = @(Find-Element $w -Type Edit | Where-Object { $_.Current.IsEnabled })
    Check 'Editor: aggiunta utente crea una riga' ($userEdit.Count -ge 4) ("campi: " + $userEdit.Count)
}
Step 'Editor: accordion sessione e script' {
    $w = W
    $g = @(Find-Element $w -Name 'Sessione e script' -Type Group)[0]
    $before = (Find-Element $w -Type Edit).Count
    $g.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    $after = (Find-Element (W) -Type Edit).Count
    Check 'Editor: accordion si apre (compaiono campi)' ($after -gt $before) "$before -> $after"
    (Find-Element (W) -Name 'Sessione e script' -Type Group)[0].GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
}
Shot $P 'editor-ssh'
Step 'Editor: salva' {
    $w = W
    $save = (Find-Element $w -Id 'ButtonSave')[0]
    Check 'Editor: pulsante Aggiungi abilitato' $save.Current.IsEnabled
    Do-Invoke $save | Out-Null
    $closed = Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -eq 0 } 10
    Check-NoError $P 'Salvataggio'
    Check 'Editor: si chiude dopo il salvataggio' ([bool]$closed)
}
Step 'Lista: il server nuovo e'' tra i risultati' {
    $tb = (Find-Element (W) -Id 'TbMainFilter')[0]
    Do-SetValue $tb 'UI-Test' | Out-Null
    $found = Wait-Cond { Has-Text (W) 'UI-Test-SSH' } 6
    Check 'Lista: UI-Test-SSH trovato con la ricerca' ([bool]$found)
}
Shot $P 'lista-risultato'
Step 'Lista: modifica del server' {
    $w = W
    $item = @(Find-Element $w -Id 'LvServerCards' | ForEach-Object { Find-Element $_ -Type ListItem })[0]
    if (-not $item) { $item = @(Find-Element $w -Type ListItem | Where-Object { (Find-Element $_ -Name 'UI-Test-SSH' -Type Text).Count -gt 0 })[0] }
    Check 'Lista: riga del server presente' ($item -ne $null)
    Do-Select $item | Out-Null
    Start-Sleep -Milliseconds 500
    Click-Text (W) 'Modifica'
    $ok = Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 }
    Check-NoError $P 'Modifica'
    Check 'Modifica: editor aperto' ([bool]$ok)
    $w = W
    $name = After-Text $w 'Nome' 'Edit'
    $val = $name.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    Check 'Modifica: nome caricato' ($val -eq 'UI-Test-SSH') $val
    Do-SetValue $name 'UI-Test-SSH-2' | Out-Null
    Start-Sleep -Milliseconds 400
    Do-Invoke (Find-Element (W) -Id 'ButtonSave')[0] | Out-Null
    $closed = Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -eq 0 } 10
    Check 'Modifica: salvata e chiusa' ([bool]$closed)
}
Step 'Lista: nome aggiornato' {
    $tb = (Find-Element (W) -Id 'TbMainFilter')[0]
    Do-SetValue $tb 'UI-Test-SSH-2' | Out-Null
    $found = Wait-Cond { Has-Text (W) 'UI-Test-SSH-2' } 6
    Check 'Lista: nome modificato visibile' ([bool]$found)
}
Step 'Lista: elimina' {
    $w = W
    $item = @(Find-Element $w -Type ListItem | Where-Object { (Find-Element $_ -Name 'UI-Test-SSH-2' -Type Text).Count -gt 0 })[0]
    Do-Select $item | Out-Null
    Start-Sleep -Milliseconds 400
    Click-Text (W) 'Elimina'
    Start-Sleep -Milliseconds 800
    Shot $P 'elimina-conferma'
    $w = W
    $ok = Find-Element $w -Type Button | Where-Object { $_.Current.IsEnabled } | ForEach-Object { $_.Current.Name + '|' + (Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join ',' }
    Write-Host ("  pulsanti dopo Elimina: " + (($ok | Select-Object -Last 8) -join ' ; '))
}
Write-Host "---- fine parte 1 (pid $P) ----"
$script:Results | Format-Table -AutoSize | Out-String

