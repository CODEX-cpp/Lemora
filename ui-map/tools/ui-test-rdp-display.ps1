# Sessione dalla Home come scheda in alto (server finto su 127.0.0.1:5901: python ui-map\tools\fake-servers\vnc_server.py)
param([string]$Proto = 'RDP', [int]$Port = 3389, [string]$SessName = 'Sessione-Prova')
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
if (-not $env:LEMORA_SHOT_WINDOW) { $env:LEMORA_SHOT_WINDOW = 'main' }
$env:LEMORA_DEMO_OFF = '1'; $env:LEMORA_SHOT = 'C:\_dev\_projects\_repos\1Remote\ui-map\_shots\inapp_sessione_' + $env:LEMORA_SHOT_WINDOW + '.png'; $env:LEMORA_SHOT_DELAY = $(if ($env:LEMORA_SHOT_WINDOW -eq 'main') { '36000' } else { '60000' })
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


function Dlg { @(Find-Element (W) -Type Window | Where-Object { (Find-Element $_ -Type Text | Where-Object { $_.Current.Name -like 'Impostazioni*' }).Count -gt 0 })[0] }
function Has-Btn2($Root, [string]$Label) { (Find-Element $Root -Name $Label -Type Button).Count -gt 0 }
Step 'rdp-pulsanti' {
    Start-Sleep -Milliseconds 1500
    foreach ($b in 'Impostazioni schermo', 'Impostazioni avanzate', 'Stacca in finestra', 'Picture in picture', 'Schermo intero', 'Ctrl + Alt + Canc', 'Riconnetti', 'Copia indirizzo') { Check "RDP: pulsante '$b'" (Has-Btn2 (W) $b) }
    Check "RDP: niente piu pulsante 'Schermo' a tendina" (-not (@(Find-Element (W) -Type Button | Where-Object { $_.Current.Name -like 'Schermo ?' -or $_.Current.Name -like 'Schermo ' + [char]0x25BE }).Count))
}
Step 'pannello-schermo' {
    Do-Invoke (@(Find-Element (W) -Name 'Impostazioni schermo' -Type Button)[0]) | Out-Null
    $d = $null; for ($i = 0; $i -lt 20 -and -not $d; $i++) { Start-Sleep -Milliseconds 400; $d = Dlg }
    Check 'Pannello schermo: si apre' ([bool]$d)
    if ($d) {
        $t = @(Find-Element $d -Type Text | ForEach-Object { $_.Current.Name })
        Write-Host ('  testi schermo: ' + (($t | Select-Object -First 30) -join ' | '))
        Check 'Pannello schermo: contiene Risoluzione' ($t -contains 'Risoluzione')
        Check 'Pannello schermo: non contiene le opzioni avanzate (Appunti)' (-not ($t -contains 'Appunti'))
        Check 'Pannello schermo: selettore monitor (titolo Monitor)' ($t -contains 'Monitor')
        Check 'Pannello schermo: pulsante Rileva' (@(Find-Element $d -Name 'Rileva' -Type Button).Count -eq 1)
        Check 'Pannello schermo: opzione segui i monitor' (@(Find-Element $d -Type CheckBox | Where-Object { $_.Current.Name -like 'Adatta la sessione ai monitor*' }).Count -eq 1)
        Win-Shot $d 'pannello_monitor'
    }
}
Step 'pannello-avanzate' {
    Do-Invoke (@(Find-Element (W) -Name 'Impostazioni avanzate' -Type Button)[0]) | Out-Null
    $d = $null; for ($i = 0; $i -lt 20 -and -not $d; $i++) { Start-Sleep -Milliseconds 400; $d = @(Find-Element (W) -Type Window | Where-Object { (Find-Element $_ -Type Text | Where-Object { $_.Current.Name -like 'Impostazioni avanzate*' }).Count -gt 0 })[0] }
    Check 'Pannello avanzate: si apre' ([bool]$d)
    if ($d) {
        $t = @(Find-Element $d -Type Text | ForEach-Object { $_.Current.Name })
        Write-Host ('  testi avanzate: ' + (($t | Select-Object -First 30) -join ' | '))
        Check 'Pannello avanzate: contiene Appunti' (($t -contains 'Appunti') -or ($t -contains 'Clipboard'))
        Check 'Pannello avanzate: non contiene Risoluzione' (-not ($t -contains 'Risoluzione'))
        $cb = @(Find-Element $d -Type CheckBox | Where-Object { $_.Current.Name -eq 'Combinazioni di tasti' })[0]
        if ($cb) { $st = $cb.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState; Check 'Default: combinazioni di tasti disattivate' ($st -eq 'Off') "stato=$st" }
        $ap = @(Find-Element $d -Type CheckBox | Where-Object { $_.Current.Name -eq 'Appunti' })[0]
        if ($ap) { $st = $ap.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState; Check 'Default: appunti attivi' ($st -eq 'On') "stato=$st"
            Do-Toggle $ap | Out-Null; Start-Sleep -Milliseconds 120
            $t2 = @(Find-Element $d -Type Text | ForEach-Object { $_.Current.Name })
            Check 'Dopo una modifica compare l avviso di riconnessione automatica' (@($t2 | Where-Object { $_ -like 'Applico le modifiche*' }).Count -ge 1)
            Start-Sleep -Milliseconds 2000
            $t3 = @(Find-Element $d -Type Text | ForEach-Object { $_.Current.Name })
            Check 'Dopo la riconnessione l avviso sparisce' (@($t3 | Where-Object { $_ -like 'Applico le modifiche*' }).Count -eq 0)
        }
        $au = @(Find-Element $d -Type ComboBox)
        $vals = $au | ForEach-Object { try { $_.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object { $_.Current.Name } } catch { '?' } }
        Write-Host ('  combo: ' + ($vals -join ' | '))
        Check 'Default: audio non riprodotto (Disabilitato / Non riprodurre)' (@($vals | Where-Object { $_ -like 'Disabil*' -or $_ -like 'Non ripro*' -or $_ -like 'Disabled' }).Count -ge 1) ($vals -join ' | ')
    }
    Check-NoError $P 'pannelli rdp'
}
Write-Host ("RIEPILOGO: {0} OK, {1} FALLITI" -f @($script:Results | Where-Object Esito -eq 'OK').Count, @($script:Results | Where-Object Esito -eq 'FALLITO').Count)
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
