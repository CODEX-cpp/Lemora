# Test reali dell'interfaccia: Home, creazione/modifica/eliminazione, Impostazioni. Solo UI Automation (nessun mouse/tastiera dell'utente).
# Uso:  . ui-map\tools\ui-test-all.ps1     (istanza isolata 'uitest' creata da zero da ui-map\_build)
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
$P = Start-Main 'uitest'
Move-Offscreen $P
function W { Main-Win $P }
# elemento (testo) dentro l'area della finestra: scarta la vecchia barra azioni che sta sotto il bordo
function In-Win($w, $e) { $r = $e.Current.BoundingRectangle; $wr = $w.Current.BoundingRectangle; $r.Y -ge $wr.Y -and $r.Bottom -le ($wr.Y + $wr.Height) -and $r.X -ge $wr.X }
function Detail-Click([string]$Text) {
    $w = W
    $t = @(Texts $w $Text | Where-Object { In-Win $w $_ })
    if ($t.Count -eq 0) { throw "pulsante '$Text' non trovato nel pannello dettaglio" }
    $b = Up-To $t[$t.Count - 1] 'Invoke'
    Do-Invoke $b | Out-Null
}
function Set-Filter([string]$Text) { Do-SetValue (Find-Element (W) -Id 'TbMainFilter')[0] $Text | Out-Null; Start-Sleep -Milliseconds 700 }
function Select-First-Row {
    $lv = (Find-Element (W) -Id 'LvServerCards')[0]
    $item = @(Find-Element $lv -Type ListItem)[0]
    if (-not $item) { return $false }
    Do-Select $item | Out-Null; Start-Sleep -Milliseconds 700; $true
}
function Edit-Values { @(Find-Element (W) -Type Edit | ForEach-Object { try { $_.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' } }) }

Write-Host "=== HOME ==="
Step 'home' {
    Check 'Home: tab Home presente' (Has-Text (W) 'Home')
    Check 'Home: PREFERITI e CRONOLOGIA' ((Has-Text (W) 'PREFERITI') -and (Has-Text (W) 'CRONOLOGIA'))
    Check 'Home: voce Nuova cartella e Opzioni' ((Has-Text (W) 'Nuova cartella') -and (Has-Text (W) 'Opzioni'))
}
Step 'home-viste' {
    foreach ($v in 'Card', 'Tree', 'List') {
        Click-Text (W) $v
        Start-Sleep -Milliseconds 1800
        $b = Up-To (Texts (W) $v)[0] 'Invoke'
        Check "Home: vista $v attivabile" (-not $b.Current.IsEnabled) "pulsante attivo dopo il clic: " + (-not $b.Current.IsEnabled)
        Check-NoError $P "vista $v"
    }
}
Step 'home-ricerca' {
    Set-Filter '10.0.0.5'
    Check 'Home: ricerca accetta testo' ((Edit-Values) -contains '10.0.0.5')
    Check 'Home: selettore tipo connessione' ((Find-Element (W) -Id 'CbQuickProtocol').Count -gt 0)
}
Shot $P 'home-ricerca'

Write-Host "=== CREAZIONE ==="
Step 'annulla' {
    $c = (Find-Element (W) -Type Custom)[0]
    $add = @($c.FindAll('Children', $script:TrueCond) | ForEach-Object { $_ } | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' })[0]
    Do-Invoke $add | Out-Null
    Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 } | Out-Null
    Do-SetValue (After-Text (W) 'Nome' 'Edit') 'NON-DEVE-ESISTERE' | Out-Null
    $w = W
    $cancel = @(Find-Element $w -Type Button | Where-Object { (Find-Element $_ -Name 'Annulla' -Type Text).Count -gt 0 -and (In-Win $w $_) })[0]
    Do-Invoke $cancel | Out-Null
    Check 'Annulla: chiude l''editor' ([bool](Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -eq 0 } 8))
    Check-NoError $P 'Annulla'
    Set-Filter 'NON-DEVE'
    Check 'Annulla: nessun server creato' (-not (Has-Text (W) 'NON-DEVE-ESISTERE'))
    Set-Filter '10.0.0.5'
}
Step 'crea' {
    $c = (Find-Element (W) -Type Custom)[0]
    $add = @($c.FindAll('Children', $script:TrueCond) | ForEach-Object { $_ } | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' })[0]
    Do-Invoke $add | Out-Null
    Check 'Aggiungi: editor aperto' ([bool](Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 }))
    Check 'Aggiungi: indirizzo precompilato con il testo cercato' ((Edit-Values) -contains '10.0.0.5')
    Check 'Aggiungi: protocollo predefinito VNC' ((After-Text (W) 'Nome host' 'Edit') -ne $null -and (Edit-Values) -contains '5900')
}
Step 'crea-ssh' {
    $lb = (Find-Element (W) -Id 'ListBoxProtocols')[0]
    $ssh = @(Find-Element $lb -Type ListItem | Where-Object { (Find-Element $_ -Name 'SSH' -Type Text).Count -gt 0 })[0]
    Do-Select $ssh | Out-Null
    Start-Sleep -Milliseconds 900
    Check 'Editor: passa al form SSH' (Has-Text (W) 'UTENTI')
    Check 'Editor: porta 22' ((Edit-Values) -contains '22')
    Do-SetValue (After-Text (W) 'Nome' 'Edit') 'UI-Test-SSH' | Out-Null
    Start-Sleep -Milliseconds 600
    Check 'Editor: riepilogo si aggiorna col nome' (Has-Text (W) 'UI-Test-SSH')
    Click-Text (W) '+ Aggiungi utente'
    Start-Sleep -Milliseconds 500
    Check 'Editor: aggiungere un utente aggiunge campi' ((@(Find-Element (W) -Type Edit)).Count -ge 8)
}
Step 'editor-form-uniforme' {
    $w = W
    $hs = @(Find-Element $w -Type Edit | Where-Object { $_.Current.IsEnabled -and $_.Current.BoundingRectangle.Height -gt 0 } | ForEach-Object { [math]::Round($_.Current.BoundingRectangle.Height) } | Sort-Object -Unique)
    Check 'Editor: campi di testo a una o due altezze (nota esclusa)' ($hs.Count -le 3) ("altezze (px a schermo): " + ($hs -join ', '))
}
Step 'editor-accordion' {
    $g = @(Find-Element (W) -Name 'Sessione e script' -Type Group)[0]
    $before = (Find-Element (W) -Type Edit).Count
    $g.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 500
    Check 'Editor: accordion Sessione e script si apre' ((Find-Element (W) -Type Edit).Count -gt $before)
    (Find-Element (W) -Name 'Sessione e script' -Type Group)[0].GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    $g2 = @(Find-Element (W) -Name 'Aspetto e tag' -Type Group)[0]
    $g2.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 400
    Check 'Editor: accordion Aspetto e tag si apre' ($g2.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -eq 'Expanded')
}
Shot $P 'editor-ssh'
Step 'salva' {
    $save = (Find-Element (W) -Id 'ButtonSave')[0]
    Check 'Editor: Aggiungi abilitato con nome e indirizzo' $save.Current.IsEnabled
    Do-Invoke $save | Out-Null
    Check 'Editor: si chiude dopo il salvataggio' ([bool](Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -eq 0 } 10))
    Check-NoError $P 'Salvataggio'
    Check 'Schede: resta solo Home' (-not (Has-Text (W) 'Nuova connessione'))
}
Step 'lista' {
    Set-Filter 'UI-Test'
    Check 'Lista: il server nuovo compare nei risultati' ([bool](Wait-Cond { Has-Text (W) 'UI-Test-SSH' } 6))
    Check 'Lista: riga selezionabile' (Select-First-Row)
    Check 'Lista: selezionare mostra il pannello dettaglio' ([bool](Wait-Cond { (@(Texts (W) 'Duplica' | Where-Object { In-Win (W) $_ })).Count -gt 0 } 4))
}
Shot $P 'lista-dettaglio'
Step 'preferito' {
    $lv = (Find-Element (W) -Id 'LvServerCards')[0]
    $cb = @(Find-Element $lv -Id 'CbFavorite')[0]
    Do-Toggle $cb | Out-Null; Start-Sleep -Milliseconds 700
    Check 'Preferito: la stella si attiva' ($cb.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On')
    Set-Filter ''
    $pane = @(Find-Element (W) -Type Pane | Where-Object { (Find-Element $_ -Id 'ListBoxFolders').Count -gt 0 })[0]
    if ($pane) { Do-Invoke (@(Find-Element $pane -Type Button)[0]) | Out-Null; Start-Sleep -Milliseconds 1300 }
    $hv = @(Find-Element (W) -Id 'HomeView'); $ht = if ($hv.Count) { (@(Find-Element $hv[0] -Type Text | ForEach-Object { $_.Current.Name }) -join ' | ') } else { 'HomeView assente' }
    Check 'Preferito: compare in PREFERITI nella Home' ($ht -like '*UI-Test-SSH*') $ht
    Shot $P 'home-preferiti'
    Set-Filter 'UI-Test'
}
Step 'modifica' {
    Set-Filter 'UI-Test'
    Select-First-Row | Out-Null
    Detail-Click 'Modifica'
    Check 'Modifica: editor aperto' ([bool](Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 }))
    Check-NoError $P 'Modifica'
    $name = After-Text (W) 'Nome' 'Edit'
    Check 'Modifica: nome caricato' ($name.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'UI-Test-SSH')
    Check 'Modifica: la scheda dell''editor dice Modifica' (Has-Text (W) 'Modifica')
    Do-SetValue $name 'UI-Test-SSH-2' | Out-Null
    Start-Sleep -Milliseconds 400
    Do-Invoke (Find-Element (W) -Id 'ButtonSave')[0] | Out-Null
    Check 'Modifica: salvata e chiusa' ([bool](Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -eq 0 } 10))
    Set-Filter 'UI-Test-SSH-2'
    Check 'Modifica: nuovo nome nella lista' ([bool](Wait-Cond { Has-Text (W) 'UI-Test-SSH-2' } 6))
}
Step 'duplica' {
    Select-First-Row | Out-Null
    Detail-Click 'Duplica'
    Check 'Duplica: apre l''editor con la copia' ([bool](Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 } 8))
    Check-NoError $P 'Duplica'
    $nm = After-Text (W) 'Nome' 'Edit'
    $val = $nm.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    Check 'Duplica: il nome della copia deriva dall''originale' ($val -like '*UI-Test-SSH-2*') $val
    Do-SetValue $nm 'UI-Test-COPIA' | Out-Null; Start-Sleep -Milliseconds 400
    Do-Invoke (Find-Element (W) -Id 'ButtonSave')[0] | Out-Null
    Check 'Duplica: copia salvata' ([bool](Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -eq 0 } 10))
    Set-Filter 'UI-Test'
    $lvs = @(Find-Element (W) -Id 'LvServerCards')
    $n = if ($lvs.Count) { @(Find-Element $lvs[0] -Type ListItem).Count } else { 0 }
    Check 'Duplica: ora le voci sono due' ($n -eq 2) "voci: $n"
}
Step 'elimina' {
    Select-First-Row | Out-Null
    Detail-Click 'Elimina'
    Start-Sleep -Milliseconds 1200
    $w = W
    $dlg = @(Find-Element $w -Type Window)[0]
    if ($dlg) {
        $txt = (@(Find-Element $dlg -Type Text | ForEach-Object { $_.Current.Name }) | Where-Object { $_ }) -join ' / '
        Write-Host "  dialogo: $($dlg.Current.Name) -> $txt"
        $btns = @(Find-Element $dlg -Type Button | Where-Object { $_.Current.IsEnabled })
        Write-Host ("  pulsanti: " + (($btns | ForEach-Object { (Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '' }) -join ' | '))
        Check 'Elimina: chiede conferma' ($dlg.Current.Name -notlike '*Errore*') $txt
        $yes = @($btns | Where-Object { $n = (Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join ''; ($n -match '^S') -or ($n -eq 'OK') -or ($n -eq 'YES') })[0]
        if ($yes) { Do-Invoke $yes | Out-Null }
    } else { Check 'Elimina: chiede conferma' $false 'nessun dialogo comparso' }
    Start-Sleep -Milliseconds 1500
    Check-NoError $P 'Elimina'
    Set-Filter 'UI-Test'
    $lvs = @(Find-Element (W) -Id 'LvServerCards')
    $n = if ($lvs.Count) { @(Find-Element $lvs[0] -Type ListItem).Count } else { 0 }
    Check 'Elimina: una voce in meno (da 2 a 1)' ($n -eq 1) "voci rimaste: $n"
}
Shot $P 'dopo-elimina'

Write-Host "=== IMPOSTAZIONI ==="
Step 'impostazioni-apri' {
    Set-Filter ''
    Click-Text (W) 'Opzioni'
    Check 'Impostazioni: scheda comparsa' ([bool](Wait-Cond { (Texts (W) 'Impostazioni').Count -ge 1 } 6))
    Check 'Impostazioni: pagina Generale' (Has-Text (W) 'LINGUA E AVVIO')
    Check 'Impostazioni: 6 categorie' (((@('Generale', 'Avvio rapido', 'Database', 'Aspetto', 'Protocolli', 'Credenziali Vault') | Where-Object { Has-Text (W) $_ }).Count) -eq 6)
}
Step 'impostazioni-pagine' {
    $marks = [ordered]@{ 'Avvio rapido' = 'Tasto di scelta rapida'; 'Database' = 'Local'; 'Aspetto' = 'Font family'; 'Protocolli' = 'Aggiungi programma'; 'Credenziali Vault' = 'Aggiungi'; 'Generale' = 'Avvia con Windows' }
    foreach ($k in $marks.Keys) {
        $nav = @(Texts (W) $k)[0]
        Do-Invoke (Up-To $nav 'Invoke') | Out-Null
        Start-Sleep -Milliseconds 900
        Check "Impostazioni: pagina $k" ([bool](Wait-Cond { Has-Text (W) $marks[$k] } 4)) ("cerco '" + $marks[$k] + "'")
        Check-NoError $P "pagina $k"
    }
}
Step 'impostazioni-toggle' {
    $cb = After-Text (W) 'Chiedi conferma prima di chiudere' 'CheckBox'
    $s0 = $cb.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    Do-Toggle $cb | Out-Null; Start-Sleep -Milliseconds 400
    $s1 = (After-Text (W) 'Chiedi conferma prima di chiudere' 'CheckBox').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    Check 'Impostazioni: interruttore cambia stato' ($s0 -ne $s1) "$s0 -> $s1"
    Do-Toggle (After-Text (W) 'Chiedi conferma prima di chiudere' 'CheckBox') | Out-Null
}
Step 'impostazioni-ricerca' {
    $tb = (Find-Element (W) -Id 'TbSearch')[0]
    Do-SetValue $tb 'chiudi' | Out-Null; Start-Sleep -Milliseconds 1200
    Check 'Ricerca: "chiudi" mostra il gruppo GENERALE' (Has-Text (W) 'GENERALE')
    Check 'Ricerca: "chiudi" non mostra Lingua' (-not (Has-VisText (W) 'Lingua'))
    Do-SetValue $tb 'tasto' | Out-Null; Start-Sleep -Milliseconds 1200
    Check 'Ricerca: "tasto" mostra AVVIO RAPIDO' (Has-Text (W) 'AVVIO RAPIDO')
    Do-SetValue $tb 'zzzz' | Out-Null; Start-Sleep -Milliseconds 900
    Check 'Ricerca: nessun risultato' (Has-TextLike (W) 'Nessuna impostazione corrisponde')
    Do-SetValue $tb '' | Out-Null; Start-Sleep -Milliseconds 900
    Check 'Ricerca: svuotata torna la pagina' ((Has-Text (W) 'Avvia con Windows') -or (Has-Text (W) 'Lingua'))
}
Step 'impostazioni-aspetto' {
    Do-Invoke (Up-To (Texts (W) 'Aspetto')[0] 'Invoke') | Out-Null; Start-Sleep -Milliseconds 800
    $light = @(Find-Element (W) -Name 'Light' -Type ListItem)[0]
    Do-Select $light | Out-Null; Start-Sleep -Milliseconds 800
    Check 'Aspetto: tema Light selezionabile' ($light.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected)
    Shot $P 'tema-light'
    $dark = @(Find-Element (W) -Name 'FluentDark' -Type ListItem)[0]
    Do-Select $dark | Out-Null; Start-Sleep -Milliseconds 800
    Check 'Aspetto: si torna a FluentDark' ($dark.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected)
    Check-NoError $P 'cambio tema'
}
Step 'impostazioni-protocolli' {
    Do-Invoke (Up-To (Texts (W) 'Protocolli')[0] 'Invoke') | Out-Null; Start-Sleep -Milliseconds 800
    $ssh = @(Find-Element (W) -Name 'SSH' -Type ListItem)[0]
    Do-Select $ssh | Out-Null; Start-Sleep -Milliseconds 900
    Check 'Protocolli: scegliendo SSH compaiono i suoi programmi' ((Has-Text (W) 'Windows Terminal') -or (Has-TextLike (W) 'PuTTY'))
    $vnc = @(Find-Element (W) -Name 'VNC' -Type ListItem)[0]
    Do-Select $vnc | Out-Null; Start-Sleep -Milliseconds 700
    Check 'Protocolli: tornando a VNC compare UltraVNC' (Has-Text (W) 'UltraVNC')
}
Step 'impostazioni-database' {
    Do-Invoke (Up-To (Texts (W) 'Database')[0] 'Invoke') | Out-Null; Start-Sleep -Milliseconds 800
    $cb = (Find-Element (W) -Id 'CbAddSource')[0]
    # il popup si chiude da solo dopo ~150 ms senza traffico UIA: si apre e si legge nello stesso giro, senza pause
    Do-Toggle $cb | Out-Null
    $found = $false
    for ($i = 0; $i -lt 12 -and -not $found; $i++) {
        $n = 0
        foreach ($win in @(Get-Windows $P)) { $n += @(Find-Element $win -Name 'MySQL').Count + @(Find-Element $win -Name 'PostgreSQL').Count }
        if ($n -ge 2) { $found = $true }
    }
    Check 'Database: menu Aggiungi mostra MySQL e PostgreSQL' $found
    if ($cb.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') { Do-Toggle $cb | Out-Null }
}
Shot $P 'impostazioni-fine'
Step 'impostazioni-launcher-off' {
    Do-Invoke (Up-To (Texts (W) 'Avvio rapido')[0] 'Invoke') | Out-Null; Start-Sleep -Milliseconds 800
    $cb = After-Text (W) "Abilita l'avvio applicazione" 'CheckBox'
    Do-Toggle $cb | Out-Null; Start-Sleep -Milliseconds 800
    Check 'Avvio rapido: spento, il resto della sezione sparisce' (-not (Has-VisText (W) 'Tasto di scelta rapida'))
}
Step 'impostazioni-chiudi' {
    $items = @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' })
    $setTab = @($items | Where-Object { (Find-Element $_ -Name 'Impostazioni' -Type Text).Count -gt 0 })[0]
    $closeBtn = @(Find-Element $setTab -Type Button)[-1]
    Do-Invoke $closeBtn | Out-Null
    Check 'Impostazioni: la X della scheda riporta alla Home' ([bool](Wait-Cond { (Texts (W) 'Impostazioni').Count -eq 0 } 8))
    Check-NoError $P 'chiusura impostazioni'
    # riaperte e chiuse con la scheda Home
    Click-Text (W) 'Opzioni'; Start-Sleep -Milliseconds 1200
    $homeTab = @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' })[0]
    Do-Invoke (Find-Element $homeTab -Type Button)[0] | Out-Null
    Check 'Impostazioni: la scheda Home riporta alla Home' ([bool](Wait-Cond { (Texts (W) 'Impostazioni').Count -eq 0 } 8))
}
Write-Host ""
Write-Host ("RIEPILOGO: {0} OK, {1} FALLITI" -f @($script:Results | Where-Object Esito -eq 'OK').Count, @($script:Results | Where-Object Esito -eq 'FALLITO').Count)
$script:Results | Where-Object Esito -eq 'FALLITO' | Format-Table -AutoSize -Wrap | Out-String
Write-Host "istanza di prova: pid $P"

