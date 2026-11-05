# Porta una istanza NUOVA e isolata di Lemora fino alla finestra principale, solo con comandi UIA.
# Uso:  . C:\_dev\_projects\_repos\1Remote\ui-map\tools\reach-main.ps1
#       $p = Start-Main 'mio-slug'      # restituisce il PID; fermare a fine lavoro con Stop-Instance $p
# Indipendente dalla lingua: usa solo struttura/AutomationId (Grid1, Grid2, LvServerCards), non i testi dei pulsanti.
# Attenzione: NON usare $pid come nome di variabile (e' di sola lettura in PowerShell).
. (Join-Path $PSScriptRoot 'uia.ps1')

function Wait-Until([scriptblock]$Cond, [int]$Seconds = 40) {
    for ($i = 0; $i -lt ($Seconds * 4); $i++) { $r = & $Cond; if ($r) { return $r }; Start-Sleep -Milliseconds 250 }
    $null
}

function Start-Main([string]$Slug) {
    $p = [int](Start-Instance $Slug | Select-Object -Last 1)
    # 1) wizard passo 0: la finestra contiene Grid1 (l'unico Button visibile e' "Successivo")
    $w = Wait-Until { Get-Windows $p | Where-Object { (Find-Element $_ -Id 'Grid1').Count -gt 0 } | Select-Object -First 1 }
    if (-not $w) { throw "Start-Main: wizard non comparso (pid $p)" }
    Move-Offscreen $p
    $next = @(Find-Element $w -Type Button)
    Do-Invoke $next[0] | Out-Null
    # 2) passo 1: compare Grid2 e i pulsanti Precedente + Fatto (Fatto e' l'ultimo Button)
    $done = Wait-Until { $b = @(Find-Element $w -Type Button); if ((Find-Element $w -Id 'Grid2').Count -gt 0 -and $b.Count -ge 2) { $b[-1] } }
    if (-not $done) { throw "Start-Main: passo 2 del wizard non comparso (pid $p)" }
    Start-Sleep -Milliseconds 800   # fine animazione di scorrimento
    Do-Invoke $done | Out-Null
    # 3) finestra principale: contiene la lista LvServerCards
    $main = Wait-Until { Get-Windows $p | Where-Object { ((Find-Element $_ -Id 'MainWindowLogo').Count -gt 0 -or (Find-Element $_ -Id 'LvServerCards').Count -gt 0) } | Select-Object -First 1 } 60
    if (-not $main) { throw "Start-Main: finestra principale non comparsa (pid $p)" }
    Move-Offscreen $p
    # 4) avvisi modali annidati (es. "Hot key already been registered" se Alt+M e' occupato dall'app dell'utente): premi il loro unico pulsante
    Start-Sleep -Milliseconds 800
    foreach ($m in @($main.FindAll('Children', (New-Object Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, [Windows.Automation.ControlType]::Window))))) {
        $b = @(Find-Element $m -Type Button)
        if ($b.Count -gt 0) { Do-Invoke $b[0] | Out-Null }
    }
    Start-Sleep -Milliseconds 500
    Restore-Foreground   # la finestra principale si prende il primo piano: lo ridiamo all'utente (altrimenti riceve i suoi tasti)
    return $p
}
# helper per le finestre successive: attese, finestra principale, menu, aggiunta connessione
function WaitFind($Root,[string]$Name='',[string]$Type='',[string]$Id='',[int]$Sec=8){ for($i=0;$i -lt $Sec*4;$i++){ $r=@(Find-Element $Root -Name $Name -Type $Type -Id $Id); if($r.Count){return $r}; Start-Sleep -Milliseconds 250 }; @() }
function MainWin([int]$p){ (Get-Windows $p | ?{ (Find-Element $_ -Id LvServerCards).Count } | select -First 1) }
function SubShots($m,[string]$prefix){ foreach($x in (Find-Element $m -Type Window)){ if($x.Current.NativeWindowHandle -ne 0){ Save-Screenshot $x ($prefix + '-' + $x.Current.NativeWindowHandle + '.png') } } }
function OpenAdd($m){ for($k=0;$k -lt 8;$k++){ $t=(Find-Element $m -Id CbPopForInExport)[0]; if((Get-StateText $t) -match 'toggle=On'){Do-Toggle $t|Out-Null;Start-Sleep -Milliseconds 400}; Do-Toggle $t|Out-Null; Start-Sleep -Milliseconds 700; $a=@(Find-Element $m -Type MenuItem | ?{ $_.Current.Name -eq 'Aggiungi' }); if($a.Count){ try{ Do-Invoke $a[0]|Out-Null; Start-Sleep -Seconds 2; return $true }catch{} } }; $false }
function FillProva($m,[string]$name='prova',[string]$addr='127.0.0.1'){ $eds=@(Find-Element $m -Type Edit); $n=($eds|?{$_.Current.AutomationId -eq 'PART_EditableTextBox'})[0]; Do-SetValue $n $name|Out-Null; $idx=0; for($i=0;$i -lt $eds.Count;$i++){ if((Get-StateText $eds[$i]) -match '"3389"'){$idx=$i} }; Do-SetValue $eds[$idx-1] $addr|Out-Null }

