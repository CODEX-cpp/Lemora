# Libreria per i test reali dell'interfaccia (UI Automation: Invoke/Toggle/Value/Select sui controlli veri, nessun clic dell'utente).
. (Join-Path $PSScriptRoot 'reach-main.ps1')
$script:Results = New-Object System.Collections.ArrayList
$script:ShotDir = 'C:\_dev\_projects\_repos\1Remote\ui-map\_shots_tmp'

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $null = $script:Results.Add([pscustomobject]@{ Test = $Name; Esito = $(if ($Ok) { 'OK' } else { 'FALLITO' }); Dettaglio = $Detail })
    Write-Host ("{0,-8} {1}  {2}" -f $(if ($Ok) { 'OK' } else { 'FALLITO' }), $Name, $Detail)
}
function Step([string]$Name, [scriptblock]$Body) {
    try { & $Body } catch { Check $Name $false ("eccezione: " + $_.Exception.Message + " (riga " + $_.InvocationInfo.ScriptLineNumber + ")") }
}
function Main-Win([int]$P) { Get-Windows $P | Where-Object { (Find-Element $_ -Id 'MainWindowLogo').Count -gt 0 } | Select-Object -First 1 }
function Shot([int]$P, [string]$Name) { $w = Main-Win $P; if ($w) { Save-Screenshot $w (Join-Path $script:ShotDir "ui_$Name.png") | Out-Null } }

# elemento Text con questo nome (esatto)
function Texts($Root, [string]$Text) { @(Find-Element $Root -Name $Text -Type Text) }
function Has-Text($Root, [string]$Text) { (Texts $Root $Text).Count -gt 0 }
function Has-TextLike($Root, [string]$Text) { (Find-Element $Root -Name $Text -Type Text -Like).Count -gt 0 }

# risale dal testo al primo antenato che ha il pattern richiesto (Invoke / Toggle / SelectionItem / ExpandCollapse)
function Up-To($e, [string]$Pattern) {
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    $x = $e
    for ($i = 0; $i -lt 8 -and $x -ne $null; $i++) {
        foreach ($pn in $x.GetSupportedPatterns()) { if ($pn.ProgrammaticName -like "$Pattern*") { return $x } }
        $x = $walker.GetParent($x)
    }
    $null
}
function Click-Text($Root, [string]$Text, [int]$Index = 0) {
    $t = @(Texts $Root $Text)
    if ($t.Count -le $Index) { throw "testo '$Text' non trovato" }
    $b = Up-To $t[$Index] 'Invoke'
    if (-not $b) { throw "nessun pulsante sopra '$Text'" }
    Do-Invoke $b | Out-Null
}
# primo controllo del tipo dato che compare DOPO il testo (ordine del documento)
function After-Text($Root, [string]$Text, [string]$Type) {
    $all = @($Root.FindAll('Descendants', $script:TrueCond) | ForEach-Object { $_ })
    $found = $false
    foreach ($e in $all) {
        $c = $e.Current
        if (-not $found -and $c.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $c.Name -eq $Text) { $found = $true; continue }
        if ($found -and $c.ControlType.ProgrammaticName -eq "ControlType.$Type") { return $e }
    }
    $null
}
function Wait-Cond([scriptblock]$Cond, [int]$Seconds = 8) { Wait-Until $Cond $Seconds }

# finestra di errore dell'app ("Lemora - Errore"): se c'e', ritorna il testo del messaggio e la chiude con OK
function Error-Dialog([int]$P) {
    $w = Main-Win $P
    if (-not $w) { return $null }
    $d = @(Find-Element $w -Type Window | Where-Object { $_.Current.Name -like '*Errore*' -or $_.Current.Name -like '*Error*' })[0]
    if (-not $d) { return $null }
    $msg = (@(Find-Element $d -Type Text | ForEach-Object { $_.Current.Name }) | Where-Object { $_ -and $_ -notlike 'Lemora*' -and $_ -ne 'OK' }) -join ' / '
    $ok = @(Find-Element $d -Type Button)[0]
    if ($ok) { Do-Invoke $ok | Out-Null }
    $msg
}
function Check-NoError([int]$P, [string]$Where) {
    $m = Error-Dialog $P
    Check "$Where : nessun errore a video" ($m -eq $null) $m
}

# NOTA: i clic con il mouse vero (SendInput / PostMessage) sono stati TOLTI da questa libreria: la finestra di test sta fuori schermo o
# dietro altre app, e un clic cieco finisce nella finestra dell'utente. Si usano solo i pattern UI Automation (Invoke/Toggle/Select/Value),
# che passano dai controlli e dai comandi veri dell'app senza toccare mouse e tastiera dell'utente.
function Is-Vis($e) { $r = $e.Current.BoundingRectangle; (-not $e.Current.IsOffscreen) -and (-not $r.IsEmpty) -and $r.Width -gt 0 }
function Vis-Texts($Root, [string]$Text) { @(Texts $Root $Text | Where-Object { Is-Vis $_ }) }
function Has-VisText($Root, [string]$Text) { (Vis-Texts $Root $Text).Count -gt 0 }

