# Helper UI Automation per mappare l'interfaccia di Lemora SENZA usare mouse né tastiera dell'utente.
# Uso:  . C:\_dev\_projects\_repos\1Remote\ui-map\tools\uia.ps1
# Tutto passa dai pattern UIA (Invoke, Toggle, ExpandCollapse, SelectionItem, Value): nessun click reale.
# Le finestre dell'app vengono spostate fuori schermo (-32000,-32000) e senza attivarle.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public class UiaWin {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@

$script:AE = [Windows.Automation.AutomationElement]
$script:TrueCond = [Windows.Automation.Condition]::TrueCondition
$script:BuildDir = 'C:\_dev\_projects\_repos\1Remote\ui-map\_build'
$script:RunRoot  = 'C:\_dev\_projects\_repos\1Remote\ui-map\_run'

# Avvia una copia isolata dell'app (dati propri, istanza propria). Restituisce il PID.
function Start-Instance([string]$Name, [string]$Demo = '') {
    $run = Join-Path $script:RunRoot $Name
    if (Test-Path $run) { Remove-Item $run -Recurse -Force }
    New-Item -ItemType Directory -Force $run | Out-Null
    robocopy $script:BuildDir $run /E /NFL /NDL /NJH /NJS /NP | Out-Null
    New-Item -ItemType File -Force (Join-Path $run 'FORCE_INTO_PORTABLE_MODE') | Out-Null   # dati accanto all'exe
    $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $run 'Lemora.exe')
    $psi.WorkingDirectory = $run
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['LEMORA_INSTANCE_ID'] = $Name    # istanza distinta per ogni agente
    $psi.EnvironmentVariables['LEMORA_HEADLESS'] = '1'         # finestre senza focus, fuori schermo, senza icona nella barra
    if ($Demo) { $psi.EnvironmentVariables['LEMORA_DEMO'] = $Demo }   # schermata su richiesta (vedi README)
    $script:UserFg = [UiaWin]::GetForegroundWindow()    # finestra dell'utente in primo piano: l'app appena avviata se la prende e riceverebbe i suoi tasti
    $p = [Diagnostics.Process]::Start($psi)
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Get-Windows $p.Id).Count -gt 0) { break } }
    Move-Offscreen $p.Id
    Restore-Foreground
    return $p.Id
}

# Restituisce il primo piano alla finestra dell'utente salvata da Start-Instance.
# BUG corretto: senza questo l'istanza fuori schermo restava in primo piano e riceveva i tasti digitati dall'utente.
function Restore-Foreground {
    $h = $script:UserFg
    if (-not $h -or $h -eq [IntPtr]::Zero) { return }
    for ($i = 0; $i -lt 4; $i++) { [UiaWin]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 250 }
}

# Chiude SOLO l'istanza con questo PID (mai per nome: c'è anche l'app dell'utente).
function Stop-Instance([int]$ProcId) { Stop-Process -Id $ProcId -Force -ErrorAction SilentlyContinue }

# Finestre di primo livello del processo (finestre principali, dialoghi, popup).
function Get-Windows([int]$ProcId) {
    $cond = New-Object Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $ProcId)
    $r = $script:AE::RootElement.FindAll('Children', $cond)
    @($r | ForEach-Object { $_ })
}

# Sposta tutte le finestre del processo fuori schermo, senza attivarle (SWP_NOSIZE|NOZORDER|NOACTIVATE).
function Move-Offscreen([int]$ProcId) {
    foreach ($w in Get-Windows $ProcId) {
        $h = [IntPtr]$w.Current.NativeWindowHandle
        if ($h -ne [IntPtr]::Zero) { [UiaWin]::SetWindowPos($h, [IntPtr]::Zero, -32000, -32000, 0, 0, 0x0015) | Out-Null }
    }
}

function Show-Windows([int]$ProcId) {
    Move-Offscreen $ProcId
    $i = 0
    foreach ($w in Get-Windows $ProcId) { "[{0}] '{1}' class={2} handle={3}" -f $i, $w.Current.Name, $w.Current.ClassName, $w.Current.NativeWindowHandle; $i++ }
}

function Get-StateText($e) {
    $s = @()
    if (-not $e.Current.IsEnabled) { $s += 'disabilitato' }
    foreach ($pn in $e.GetSupportedPatterns()) {
        switch -Wildcard ($pn.ProgrammaticName) {
            'TogglePatternIdentifiers*'        { $s += 'toggle=' + $e.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
            'SelectionItemPatternIdentifiers*' { $s += 'selezionato=' + $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
            'ExpandCollapsePatternIdentifiers*'{ $s += 'espandibile=' + $e.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState }
            'ValuePatternIdentifiers*'         { $v = $e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current; $s += 'valore=' + $(if ($v.IsReadOnly) { '(sola lettura) ' } else { '' }) + '"' + $v.Value + '"' }
        }
    }
    $s -join '; '
}

# Stampa l'albero dei controlli di una finestra (indice da Show-Windows) o di un elemento.
function Dump-Tree($Root, [int]$MaxDepth = 12, [int]$MaxLines = 600) {
    $script:lines = 0
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    function Walk($e, $d) {
        if ($script:lines -ge $MaxLines -or $d -gt $MaxDepth) { return }
        $c = $e.Current
        $type = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        $pats = ($e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace 'PatternIdentifiers\.Pattern$', '' }) -join ','
        $st = try { Get-StateText $e } catch { '' }
        "{0}[{1}] name='{2}' id='{3}' pat={4} {5}" -f ('  ' * $d), $type, $c.Name, $c.AutomationId, $pats, $st
        $script:lines++
        $ch = $walker.GetFirstChild($e)
        while ($ch -ne $null) { Walk $ch ($d + 1); $ch = $walker.GetNextSibling($ch) }
    }
    Walk $Root 0
    if ($script:lines -ge $MaxLines) { "... (troncato a $MaxLines righe: restringi con un sotto-elemento o Find-Element)" }
}

# Cerca elementi nella finestra per nome (esatto o parziale) e/o AutomationId e/o tipo (es. Button).
function Find-Element($Root, [string]$Name = '', [string]$Id = '', [string]$Type = '', [switch]$Like) {
    $all = $Root.FindAll('Descendants', $script:TrueCond)
    @($all | ForEach-Object { $_ } | Where-Object {
        $c = $_.Current
        ((-not $Name) -or ($(if ($Like) { $c.Name -like "*$Name*" } else { $c.Name -eq $Name }))) -and
        ((-not $Id) -or $c.AutomationId -eq $Id) -and
        ((-not $Type) -or $c.ControlType.ProgrammaticName -eq "ControlType.$Type")
    })
}

# Azioni: restituiscono cosa è stato fatto; dopo ogni azione usare Show-Windows per vedere se è comparsa una finestra.
function Do-Invoke($e)  { $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); 'invocato: ' + $e.Current.Name }
function Do-Toggle($e)  { $e.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); 'toggle ora: ' + $e.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
function Do-Expand($e)  { $e.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); 'espanso: ' + $e.Current.Name }
function Do-Collapse($e){ $e.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); 'chiuso: ' + $e.Current.Name }
function Do-Select($e)  { $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); 'selezionato: ' + $e.Current.Name }
function Do-SetValue($e, [string]$Text) { $e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Text); 'valore impostato: ' + $Text }
# Chiude una finestra (WindowPattern.Close) senza premere tasti.
function Do-CloseWindow($w) { $w.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); 'finestra chiusa: ' + $w.Current.Name }

# Salva l'inventario dei controlli interattivi di una finestra: una riga "Tipo|Nome|Id" per controllo.
# Serve al verificatore di copertura (check-coverage.ps1): ogni riga deve comparire nel file .md della finestra.
$script:InteractiveTypes = 'Button','CheckBox','ComboBox','Edit','Hyperlink','ListItem','MenuItem','RadioButton','Slider','Spinner','SplitButton','TabItem','TreeItem','DataItem','Thumb','ScrollBar'
function Save-Inventory($Root, [string]$Path) {
    $rows = @()
    foreach ($e in $Root.FindAll('Descendants', $script:TrueCond)) {
        $t = $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        if ($script:InteractiveTypes -contains $t) { $rows += ('{0}|{1}|{2}' -f $t, $e.Current.Name, $e.Current.AutomationId) }
    }
    $rows | Sort-Object -Unique | Set-Content -Path $Path -Encoding UTF8
    "inventario salvato: $($rows.Count) controlli ($((($rows | Sort-Object -Unique)).Count) unici) -> $Path"
}

# Cattura una finestra (anche fuori schermo o non in primo piano) in un PNG, con PrintWindow (PW_RENDERFULLCONTENT).
# Uso: Save-Screenshot (Get-Windows $pid)[0] 'C:\...\ui-map\screenshots\<slug>\01-stato-iniziale.png'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class UiaShot {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
function Save-Screenshot($Window, [string]$Path) {
    $h = [IntPtr]$Window.Current.NativeWindowHandle
    $r = New-Object UiaShot+RECT
    [UiaShot]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    if ($w -le 0 -or $ht -le 0) { return 'finestra senza dimensioni: screenshot non fatto' }
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $bmp = New-Object Drawing.Bitmap $w, $ht
    $g = [Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    $ok = [UiaShot]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($Path, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "screenshot salvato ($w x $ht, PrintWindow=$ok): $Path"
}

# ---- Screenshot con popup (menu, tendine) -------------------------------------------------------------------
# I popup WPF sono finestre Win32 di primo livello SENZA proprietario (classe HwndWrapper, UIA 'Popup'). Hanno handle
# (anche le tendine delle ComboBox), ma il NativeWindowHandle UIA puo' essere 0: si trovano con EnumWindows per PID.
# Si cattura madre + ogni altra finestra visibile del processo con PrintWindow (in Lemora tutto e' in-page: sono solo popup).
# ATTENZIONE: la guardia headless sposta anche i popup a (-32000,-32000), cioe' TUTTI SOVRAPPOSTI nello stesso punto, quindi
# GetWindowRect non da' la posizione vera. La posizione si ricostruisce con -Anchors, una voce per popup IN ORDINE DI APERTURA
# (EnumWindows da' l'ordine Z, dall'alto: lo si rovescia):  @{El=<elemento che lo apre>; Parent=-1|<indice popup>; Mode='Bottom'|'Right'}
#   Bottom = angolo in basso a sinistra dell'elemento (Popup WPF, Placement=Bottom di default); Right = angolo in alto a destra (sottomenu).
#   Parent = -1 se l'elemento sta nella finestra madre, altrimenti l'indice (0,1,...) del popup che lo contiene.
# Senza -Anchors i popup restano sovrapposti all'angolo in alto a sinistra della madre.
# Uso: apri il popup (Do-Toggle/Do-Expand), tieni del traffico UIA (i popup si chiudono dopo ~150 ms senza), poi SUBITO
#      Save-ScreenshotWithPopups $m 'C:\...\NN-popup-xxx.png' -Anchors @(@{El=$btn;Parent=-1;Mode='Bottom'})
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public class UiaEnum {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  public static List<IntPtr> Others(uint pid, IntPtr main) {
    var l = new List<IntPtr>();
    EnumWindows((h, _) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h) && h != main) l.Add(h); return true; }, IntPtr.Zero);
    l.Reverse(); return l;   // ordine di apertura (il piu' vecchio per primo)
  }
}
'@
function Save-ScreenshotWithPopups($Window, [string]$Path, [object[]]$Anchors = @()) {
    $main = [IntPtr]$Window.Current.NativeWindowHandle
    $null = $Window.FindFirst('Descendants', $script:TrueCond)    # traffico UIA: evita la chiusura dei popup
    $hs = @($main) + @([UiaEnum]::Others([uint32]$Window.Current.ProcessId, $main))
    $items = @()
    foreach ($h in $hs) {
        $r = New-Object UiaShot+RECT; [UiaShot]::GetWindowRect($h, [ref]$r) | Out-Null
        $w = [int]($r.R - $r.L); $ht = [int]($r.B - $r.T)
        if ($w -le 0 -or $ht -le 0) { continue }
        $b = New-Object Drawing.Bitmap $w, $ht
        $g = [Drawing.Graphics]::FromImage($b); $g.Clear([Drawing.Color]::Transparent); $hdc = $g.GetHdc()
        [UiaShot]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
        $items += [pscustomobject]@{ B = $b; L0 = [int]$r.L; T0 = [int]$r.T; X = 0; Y = 0 }   # X,Y = posizione nell'immagine finale
    }
    for ($i = 1; $i -lt $items.Count; $i++) {                     # $items[i] = popup i-1
        $a = if ($i - 1 -lt $Anchors.Count) { $Anchors[$i - 1] } else { $null }
        if (-not $a) { continue }
        $br = $a.El.Current.BoundingRectangle; $par = $items[$a.Parent + 1]
        $ex = [int]($br.Left - $par.L0) + $par.X; $ey = [int]($br.Top - $par.T0) + $par.Y
        if ($a.Mode -eq 'Right') { $ex += [int]$br.Width } else { $ey += [int]$br.Height }
        $items[$i].X = $ex; $items[$i].Y = $ey
    }
    $minX = ($items | % { $_.X } | Measure-Object -Minimum).Minimum; $minY = ($items | % { $_.Y } | Measure-Object -Minimum).Minimum
    $maxX = ($items | % { $_.X + $_.B.Width } | Measure-Object -Maximum).Maximum; $maxY = ($items | % { $_.Y + $_.B.Height } | Measure-Object -Maximum).Maximum
    $out = New-Object Drawing.Bitmap ([int]($maxX - $minX)), ([int]($maxY - $minY))
    $g = [Drawing.Graphics]::FromImage($out); $g.Clear([Drawing.Color]::White)
    foreach ($it in $items) { $g.DrawImageUnscaled($it.B, [int]($it.X - $minX), [int]($it.Y - $minY)) }   # madre per prima, popup sopra
    $g.Dispose(); New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $out.Save($Path, [Drawing.Imaging.ImageFormat]::Png); $out.Dispose(); $items | % { $_.B.Dispose() }
    "screenshot con popup: $($items.Count - 1) popup, $($maxX-$minX) x $($maxY-$minY): $Path"
}

# ---- Screenshot di finestre trasparenti (Launcher) ----------------------------------------------------------
# PrintWindow rende NERO (0,0,0) dove la finestra e' trasparente: quei pixel diventano grigio chiaro (i pixel veri sono opachi).
function Save-ScreenshotLayered($Window, [string]$Path) {
    $h = [IntPtr]$Window.Current.NativeWindowHandle
    $r = New-Object UiaShot+RECT; [UiaShot]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    if ($w -le 0 -or $ht -le 0) { return 'finestra senza dimensioni: screenshot non fatto' }
    $bmp = New-Object Drawing.Bitmap $w, $ht
    $g = [Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [UiaShot]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
    $bg = [Drawing.Color]::FromArgb(236, 236, 236)
    for ($x = 0; $x -lt $w; $x++) { for ($y = 0; $y -lt $ht; $y++) { $c = $bmp.GetPixel($x, $y); if ($c.R -eq 0 -and $c.G -eq 0 -and $c.B -eq 0) { $bmp.SetPixel($x, $y, $bg) } } }
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $bmp.Save($Path, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "screenshot (layered) salvato ($w x $ht): $Path"
}
