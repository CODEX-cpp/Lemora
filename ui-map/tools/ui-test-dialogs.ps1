# Dialoghi: modifica database (MySQL/PostgreSQL/SQLite) e conferma eliminazione. Solo UI Automation.
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
$P = Start-Main 'uitest'
Move-Offscreen $P
function W { Main-Win $P }
function Dlg { @(Get-Windows $P | Where-Object { (Find-Element $_ -Id 'MainWindowLogo').Count -eq 0 })[0] }
function In-Win($w, $e) { $r = $e.Current.BoundingRectangle; $wr = $w.Current.BoundingRectangle; $r.Y -ge $wr.Y -and $r.Bottom -le ($wr.Y + $wr.Height) -and $r.X -ge $wr.X }

Write-Host "=== DIALOGHI DATABASE ==="
Click-Text (W) 'Opzioni'; Start-Sleep -Milliseconds 1200
Do-Invoke (Up-To (Texts (W) 'Database')[0] 'Invoke') | Out-Null; Start-Sleep -Milliseconds 800

foreach ($kind in 'MySQL', 'PostgreSQL') {
    Step "db-$kind" {
        $cb = (Find-Element (W) -Id 'CbAddSource')[0]
        Do-Toggle $cb | Out-Null
        $btn = $null
        for ($i = 0; $i -lt 20 -and -not $btn; $i++) {
            foreach ($win in @(Get-Windows $P)) {
                $t = @(Find-Element $win -Name $kind | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -or (Up-To $_ 'Invoke') })
                if ($t.Count) { $btn = Up-To $t[0] 'Invoke'; break }
            }
        }
        if (-not $btn) { throw "voce $kind non trovata nel menu" }
        Do-Invoke $btn | Out-Null
        $d = $null
        for ($i = 0; $i -lt 30 -and -not $d; $i++) { Start-Sleep -Milliseconds 300; $d = Dlg }
        Check "$kind : si apre la finestra" ($d -ne $null)
        Start-Sleep -Milliseconds 800
        $d = Dlg
        Check "$kind : titolo, 6 campi e pulsanti" ((Has-Text $d $kind) -and ((@(Find-Element $d -Type Edit)).Count -ge 5) -and (Has-Text $d 'Salva') -and (Has-Text $d 'Annulla'))
        Save-Screenshot $d (Join-Path $script:ShotDir "dlg_$($kind)_vuoto.png") | Out-Null
        # campi vuoti: errore inline, Salva disabilitato
        $save = Up-To (Texts $d 'Salva')[0] 'Invoke'
        Check "$kind : Salva disabilitato con campi non validi" (-not $save.Current.IsEnabled)
        # compila tutto
        $edits = @(Find-Element $d -Type Edit)
        $vals = @('Test-DB', '127.0.0.1', '3306', 'db1', 'utente')
        for ($i = 0; $i -lt $vals.Count; $i++) { try { Do-SetValue $edits[$i] $vals[$i] | Out-Null } catch { } }
        $pw = @(Find-Element $d -Id 'CipherTextBox')
        if ($pw.Count) { Do-SetValue $pw[0] 'segreta' | Out-Null }
        Start-Sleep -Milliseconds 700
        $d = Dlg
        Save-Screenshot $d (Join-Path $script:ShotDir "dlg_$($kind)_compilato.png") | Out-Null
        $save = Up-To (Texts $d 'Salva')[0] 'Invoke'
        Check "$kind : Salva abilitato con dati validi" $save.Current.IsEnabled
        # Annulla chiude senza creare
        Do-Invoke (Up-To (Texts $d 'Annulla')[0] 'Invoke') | Out-Null
        Check "$kind : Annulla chiude la finestra" ([bool](Wait-Cond { $null -eq (Dlg) } 6))
        Check-NoError $P "dialogo $kind"
    }
}
Step 'db-modifica-local' {
    $edit = @(Texts (W) 'Modifica')
    $row = @($edit | Where-Object { Is-Vis $_ } | Select-Object -First 1)
    if (-not $row) { $row = $edit }
    Do-Invoke (Up-To $row[0] 'Invoke') | Out-Null
    $d = $null
    for ($i = 0; $i -lt 30 -and -not $d; $i++) { Start-Sleep -Milliseconds 300; $d = Dlg }
    Check 'SQLite (Local): si apre la finestra di modifica' ($d -ne $null)
    Start-Sleep -Milliseconds 700
    $d = Dlg
    Save-Screenshot $d (Join-Path $script:ShotDir 'dlg_sqlite.png') | Out-Null
    Check 'SQLite: titolo, nome, percorso e Salva' ((Has-Text $d 'SQLite') -and (Has-Text $d 'Salva'))
    Do-Invoke (Up-To (Texts $d 'Salva')[0] 'Invoke') | Out-Null
    Check 'SQLite: Salva chiude la finestra' ([bool](Wait-Cond { $null -eq (Dlg) } 6))
}

Write-Host "=== CONFERMA ELIMINAZIONE ==="
Step 'conferma-eliminazione' {
    Do-Invoke (Up-To (Texts (W) 'Home')[0] 'Invoke') | Out-Null; Start-Sleep -Milliseconds 1500
    $c = (Find-Element (W) -Type Custom)[0]
    $add = @($c.FindAll('Children', $script:TrueCond) | ForEach-Object { $_ } | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' })[0]
    Do-Invoke $add | Out-Null
    Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 } | Out-Null
    Do-SetValue (After-Text (W) 'Nome' 'Edit') 'Da-Eliminare' | Out-Null
    Do-SetValue (After-Text (W) 'Nome host' 'Edit') '10.1.1.1' | Out-Null
    Start-Sleep -Milliseconds 600
    Do-Invoke (Find-Element (W) -Id 'ButtonSave')[0] | Out-Null
    Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -eq 0 } 10 | Out-Null
    Do-SetValue (Find-Element (W) -Id 'TbMainFilter')[0] 'Da-Eliminare' | Out-Null; Start-Sleep -Milliseconds 900
    $item = @(Find-Element (Find-Element (W) -Id 'LvServerCards')[0] -Type ListItem)[0]
    Do-Select $item | Out-Null; Start-Sleep -Milliseconds 800
    $w = W
    $t = @(Texts $w 'Elimina' | Where-Object { In-Win $w $_ })
    Do-Invoke (Up-To $t[$t.Count - 1] 'Invoke') | Out-Null
    Start-Sleep -Milliseconds 1200
    Shot $P 'conferma-eliminazione'
    $w = W
    $dlg = @(Find-Element $w -Type Window)[0]
    Check 'Conferma: compare il riquadro' ($dlg -ne $null)
    $btns = @(Find-Element $dlg -Type Button)
    Check 'Conferma: due pulsanti (Si / No)' ($btns.Count -eq 2)
    $no = @($btns | Where-Object { ((Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '') -match '^N' })[0]
    Do-Invoke $no | Out-Null
    Start-Sleep -Milliseconds 800
    Check 'Conferma: No lascia il server' (Has-Text (W) 'Da-Eliminare')
}
Write-Host ""
Write-Host ("RIEPILOGO: {0} OK, {1} FALLITI" -f @($script:Results | Where-Object Esito -eq 'OK').Count, @($script:Results | Where-Object Esito -eq 'FALLITO').Count)
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
