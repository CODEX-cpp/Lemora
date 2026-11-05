# Sessione dalla Home come scheda in alto (server finto su 127.0.0.1:5901: python ui-map\tools\fake-servers\vnc_server.py)
param([string]$Proto = 'VNC', [int]$Port = 5901, [string]$SessName = 'Sessione-Prova')
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
if (-not $env:LEMORA_SHOT_WINDOW) { $env:LEMORA_SHOT_WINDOW = 'main' }
$env:LEMORA_DEMO = 'noop'; $env:LEMORA_SHOT = 'C:\_dev\_projects\_repos\1Remote\ui-map\_shots\inapp_sessione_' + $env:LEMORA_SHOT_WINDOW + '.png'; $env:LEMORA_SHOT_DELAY = $(if ($env:LEMORA_SHOT_WINDOW -eq 'main') { '36000' } else { '60000' })
Remove-Item $env:LEMORA_SHOT -ErrorAction SilentlyContinue
$P = Start-Main 'uitest'
# finestra visibile: servono screenshot veri dello schermo
$hm = [IntPtr](Main-Win $P).Current.NativeWindowHandle
[UiaWin]::SetWindowPos($hm, [IntPtr]::Zero, 80, 40, 1250, 800, 0x0044) | Out-Null
Start-Sleep -Milliseconds 800
function Has-Btn($Root, [string]$Label) { (Find-Element $Root -Name $Label -Type Button).Count -gt 0 }
function W { Main-Win $P }
function In-Win($w, $e) { $r = $e.Current.BoundingRectangle; $wr = $w.Current.BoundingRectangle; $r.Y -ge $wr.Y -and $r.Bottom -le ($wr.Y + $wr.Height) -and $r.X -ge $wr.X }
function Tab-Names { @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' -and (Find-Element $_ -Name '+' -Type Text).Count -eq 0 } | ForEach-Object { (Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '' }) }

Write-Host "=== BARRA SESSIONE, FINESTRA STACCATA, PIP ==="
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

function Screen-Shot([string]$Name) {
    $w = Main-Win $P
    $r = $w.Current.BoundingRectangle
    $bmp = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
    $f = Join-Path $script:ShotDir "ui_$Name.png"; $bmp.Save($f, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); Write-Host "  screenshot: $f"
}
function Win-Shot($w, [string]$Name) {
    $r = $w.Current.BoundingRectangle
    $bmp = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
    $f = Join-Path $script:ShotDir "ui_$Name.png"; $bmp.Save($f, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); Write-Host "  screenshot: $f"
}
function Detached-Win { @(Get-Windows $P | Where-Object { $_.Current.Name -like "$SessName - Lemora" })[0] }
function Click-Btn([string]$Label) {
    $t = @(Find-Element (W) -Name $Label -Type Button | Where-Object { In-Win (W) $_ })
    if (-not $t.Count) { return $false }
    Do-Invoke $t[0] | Out-Null; $true
}
Step 'barra' {
    $w = W
    Start-Sleep -Milliseconds 800
    Write-Host ('  qualsiasi tipo: ' + ((Find-Element $w -Name 'Stacca in finestra') | ForEach-Object { $_.Current.ControlType.ProgrammaticName + '/' + $_.Current.ClassName }) -join ',')
    Write-Host ('  tipi con Riconnetti: ' + ((Find-Element $w -Name 'Riconnetti*' -Like) | ForEach-Object { $_.Current.ControlType.ProgrammaticName + '/' + $_.Current.Name }) -join ',')
    Write-Host ('  bottoni: ' + ((Find-Element $w -Type Button | ForEach-Object { $_.Current.Name }) -join ' | '))
    Write-Host ('  testi: ' + ((Find-Element $w -Type Text | ForEach-Object { $_.Current.Name } | Select-Object -First 40) -join ' | '))
    foreach ($b in 'Stacca in finestra', 'Picture in picture', 'Riconnetti', 'Copia indirizzo') { Check "Barra: pulsante '$b'" (Has-Btn $w $b) }
    Check "Barra: stato" (Has-TextLike $w 'Connesso*' -or (Has-TextLike $w 'Connessione*'))
    Check "Barra: nome sessione" (Has-Text $w $SessName)
    Check 'Nessuna riga di stato in basso' (-not (Has-TextLike $w '*sessione aperta') -and -not (Has-TextLike $w '*sessioni aperte'))
    Screen-Shot 'barra-sessione'
}
Step 'stacca' {
    Check 'Stacca: pulsante premuto' (Click-Btn 'Stacca in finestra')
    $dw = $null; for ($i = 0; $i -lt 20 -and -not $dw; $i++) { Start-Sleep -Milliseconds 300; $dw = Detached-Win }
    Check 'Stacca: compare la finestra "<nome> - Lemora"' ([bool]$dw)
    Check 'Stacca: la scheda mostra il riquadro "Sessione in una finestra separata"' (Has-Text (W) 'Sessione in una finestra separata')
    Check 'Stacca: scheda col simbolo' (@(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' -and (Find-Element $_ -Name '+' -Type Text).Count -eq 0 } | ForEach-Object { (Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '' } | Where-Object { $_ -like "*$SessName" -and $_ -like '⧉*' }).Count -gt 0)
    if ($dw) {
        Check 'Finestra staccata: linguetta Opzioni (nella finestra trasparente sopra lo schermo)' (@(Get-Windows $P | Where-Object { Has-TextLike $_ 'Opzioni*' }).Count -ge 1)
        Win-Shot $dw 'sessione-staccata'
        Start-Sleep -Milliseconds 4000
    }
    Check-NoError $P 'Stacca'
}
Step 'pip' {
    Check 'PiP: pulsante premuto' (Click-Btn 'Picture in picture')
    Start-Sleep -Milliseconds 1200
    $dw = Detached-Win
    Check 'PiP: la finestra esiste ancora' ([bool]$dw)
    if ($dw) {
        $r = $dw.Current.BoundingRectangle
        Check 'PiP: finestra piccola (circa 380x238)' ($r.Width -lt 500 -and $r.Height -lt 330) ("{0}x{1}" -f $r.Width, $r.Height)
        Win-Shot $dw 'sessione-pip'
    }
    Check-NoError $P 'PiP'
}
Step 'riattacca' {
    Check 'Riattacca: pulsante premuto' (Click-Btn "Riattacca all'app")
    Start-Sleep -Milliseconds 1500
    Check 'Riattacca: la finestra staccata e chiusa' (-not (Detached-Win))
    Check 'Riattacca: la barra e di nuovo la sessione' (Has-Btn (W) 'Stacca in finestra')
    Check 'Riattacca: niente riquadro separata' (-not (Has-Text (W) 'Sessione in una finestra separata'))
    Screen-Shot 'sessione-riattaccata'
    Check-NoError $P 'Riattacca'
}
Step 'chiudi' {
    $tabs = @(Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' -and (Find-Element $_ -Name '+' -Type Text).Count -eq 0 })
    $close = @(Find-Element $tabs[1] -Type Button)[-1]
    Do-Invoke $close | Out-Null
    Start-Sleep -Milliseconds 1500
    $dlg = @(Find-Element (W) -Type Window)[0]
    if ($dlg) {
        $yes = @(Find-Element $dlg -Type Button | Where-Object { ((Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '') -match '^S' })[0]
        if ($yes) { Do-Invoke $yes | Out-Null; Start-Sleep -Milliseconds 1500 }
    }
    Check 'Chiudi: la scheda sparisce' (-not ((Tab-Names) -contains $SessName)) ("schede: " + ((Tab-Names) -join ' | '))
    Check-NoError $P 'chiusura'
}
Write-Host ""
Write-Host ("RIEPILOGO: {0} OK, {1} FALLITI" -f @($script:Results | Where-Object Esito -eq 'OK').Count, @($script:Results | Where-Object Esito -eq 'FALLITO').Count)
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
