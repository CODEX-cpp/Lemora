# Quante volte chiede la password una connessione VNC con server che vuole la password VNC (127.0.0.1:5902, vnc_variants.py)?
. (Join-Path $PSScriptRoot 'ui-test-lib.ps1')
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
Start-Sleep 1
$P = Start-Main 'uitest'
Move-Offscreen $P
function W { Main-Win $P }
function In-Win($w, $e) { $r = $e.Current.BoundingRectangle; $wr = $w.Current.BoundingRectangle; $r.Y -ge $wr.Y -and $r.Bottom -le ($wr.Y + $wr.Height) -and $r.X -ge $wr.X }
function Pwd-Dialogs { @(Find-Element (W) -Type Window | Where-Object { $_.Current.Name -like '*VNC*' }) }

$c = (Find-Element (W) -Type Custom)[0]
$add = @($c.FindAll('Children', $script:TrueCond) | ForEach-Object { $_ } | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' })[0]
Do-Invoke $add | Out-Null
Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -gt 0 } | Out-Null
$lb = (Find-Element (W) -Id 'ListBoxProtocols')[0]
Do-Select (@(Find-Element $lb -Type ListItem | Where-Object { (Find-Element $_ -Name 'VNC' -Type Text).Count -gt 0 })[0]) | Out-Null
Start-Sleep -Milliseconds 900
Do-SetValue (After-Text (W) 'Nome' 'Edit') 'Vnc-Password' | Out-Null
Do-SetValue (After-Text (W) 'Nome host' 'Edit') '127.0.0.1' | Out-Null
Do-SetValue (After-Text (W) 'Porta' 'Edit') '5902' | Out-Null
Start-Sleep -Milliseconds 500
Do-Invoke (Find-Element (W) -Id 'ButtonSave')[0] | Out-Null
Wait-Cond { (Find-Element (W) -Id 'ButtonSave').Count -eq 0 } 10 | Out-Null
Do-SetValue (Find-Element (W) -Id 'TbMainFilter')[0] 'Vnc-Password' | Out-Null; Start-Sleep -Milliseconds 900
Do-Select (@(Find-Element (Find-Element (W) -Id 'LvServerCards')[0] -Type ListItem)[0]) | Out-Null; Start-Sleep -Milliseconds 800
$w = W
$t = @(Texts $w 'Connetti' | Where-Object { In-Win $w $_ })
Do-Invoke (Up-To $t[$t.Count - 1] 'Invoke') | Out-Null

$answered = 0
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $d = @(Pwd-Dialogs)
    if ($d.Count -gt 0) {
        $dlg = $d[0]
        $pw = @(Find-Element $dlg -Id 'CipherTextBox')
        Write-Host ("  [t={0}s] finestra password n.{1}: titolo='{2}' campi={3}" -f ($i / 2), ($answered + 1), $dlg.Current.Name, @(Find-Element $dlg -Type Edit).Count)
        if ($pw.Count) { Do-SetValue $pw[0] 'segreta' | Out-Null }
        Start-Sleep -Milliseconds 400
        $ok = @(Find-Element $dlg -Type Button | Where-Object { ((Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '') -eq 'OK' })[0]
        if ($ok) { Do-Invoke $ok | Out-Null }
        $answered++
        Start-Sleep -Milliseconds 1500
    }
}
Write-Host "RISULTATO: la password e' stata chiesta $answered volta/e"
Write-Host ("schede: " + ((Find-Element (W) -Type DataItem | Where-Object { $_.Current.Name -like '*MainTabItem' -and (Find-Element $_ -Name '+' -Type Text).Count -eq 0 } | ForEach-Object { (Find-Element $_ -Type Text | ForEach-Object { $_.Current.Name }) -join '' }) -join ' | '))
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\uitest\*' } | Stop-Process -Force
