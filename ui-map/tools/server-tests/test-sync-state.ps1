# Prova dello stato di sincronizzazione dei PC (pcs.share_state / share_changed_at, evento 'sync-setting') sul server vero:
# processo a parte, porta casuale libera, cartella dati usa e getta (la porta 25353 e i dati veri non si toccano). Esito: PASS/FAIL per ogni controllo.
param([string]$Exe = 'C:\_dev\_projects\_repos\1Remote\Server\bin\Release\net10.0\Lemora.Server.exe')
$exe = $Exe
$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $Port = $l.LocalEndpoint.Port; $l.Stop()
$work = Join-Path $env:TEMP ('pairing-test-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
[IO.Directory]::CreateDirectory($data) | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$dbf = Join-Path $db 'server.db'; $sq = Join-Path $PSScriptRoot 'shared-db.py'
$base = "https://127.0.0.1:$Port"
$adminPw = 'Password-di-prova-123'
$script:pass = 0; $script:fail = 0
"porta di prova: $Port"

function Call($method, $path, $json = $null, [bool]$csrf = $true, [string]$bearer = '', [bool]$cookies = $false, [string]$caps = '') {
    $a = @('-k', '-s', '-X', $method, '-w', '\n%{http_code}')
    if ($cookies) { $a += @('-b', $jar, '-c', $jar) }
    if ($csrf) { $a += @('-H', 'X-Lemora: 1') }
    if ($bearer) { $a += @('-H', "Authorization: Bearer $bearer") }
    if ($caps) { $a += @('-H', "X-Lemora-Caps: $caps") }
    if ($json -ne $null) { [IO.File]::WriteAllText($body, $json, [Text.UTF8Encoding]::new($false)); $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$body") }
    $a += "$base$path"
    $out = (& curl.exe @a) -join "`n"
    $i = $out.LastIndexOf("`n")
    $text = if ($i -ge 0) { $out.Substring(0, $i) } else { '' }
    $code = [int]$out.Substring($i + 1)
    $obj = $null; try { if ($text) { $obj = $text | ConvertFrom-Json } } catch { }
    [pscustomobject]@{ Code = $code; Obj = $obj; Text = $text }
}
function Check($label, $cond, $detail = '') { if ($cond) { $script:pass++; "PASS  $label" } else { $script:fail++; "FAIL  $label   $detail" } }
function Start-Server {
    $script:proc = Start-Process $exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'out.txt') -RedirectStandardError (Join-Path $work 'err.txt')
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; $r = Call GET '/api/state'; if ($r.Code -eq 200) { return } }
    throw 'server non avviato'
}
function Sql($sql, [string[]]$p = @()) { (& python -I $sq $dbf $sql @p) -join '' }
function J($o) { $o | ConvertTo-Json -Compress -Depth 6 }
function New-Pc($name, [string]$perm = 'read', [bool]$noCopy = $false) {
    $c = (Call POST '/api/pairing/new' '{}' -cookies $true).Obj.code
    $r = Call POST '/api/pair/request' (J @{ code = $c; pc = $name; user = 'User01' })
    $null = Call POST "/api/pcs/$($r.Obj.id)/approve" (J @{ permission = $perm; noCopy = $noCopy }) -cookies $true
    $key = (Call POST '/api/pair/status' (J @{ id = $r.Obj.id; token = $r.Obj.token })).Obj.key
    [pscustomobject]@{ Id = $r.Obj.id; Key = $key; Name = $name }
}
function Share($pc, [bool]$share, [string]$caps = 'personal') { Call POST '/api/client/personal' (J @{ share = $share; items = @() }) -bearer $pc.Key -caps $caps }
function Row($pc) { @((Call GET '/api/pcs' -cookies $true).Obj) | Where-Object { $_.id -eq $pc.Id } }
function Evs($pc) { [int](Sql "SELECT COUNT(*) FROM events WHERE kind='sync-setting' AND pc_id=?" @($pc.Id)) }
function LastEv($pc) { Sql "SELECT detail FROM events WHERE kind='sync-setting' AND pc_id=? ORDER BY id DESC LIMIT 1" @($pc.Id) }

Start-Server
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
$null = Call POST '/api/setup/verify' (J @{ code = $setup }) -cookies $true
$r = Call POST '/api/setup/complete' (J @{ dbDir = $db; user = 'admin'; password = $adminPw; port = $Port; address = 'all' }) -cookies $true
Check 'configurazione' ($r.Code -eq 200) $r.Text
Start-Sleep 1
$null = Call POST '/api/login' (J @{ user = 'admin'; password = $adminPw }) -cookies $true

$w = New-Pc 'PC-W' 'write'; $w2 = New-Pc 'PC-W2' 'write'; $rd = New-Pc 'PC-READ' 'read'; $q = New-Pc 'PC-REQ' 'requests'; $nc = New-Pc 'PC-NOCOPY' 'write' $true

'--- mai visto: unknown'
$row = Row $w
Check 'PC mai sentito: shareState unknown, shareChangedAt null' ($row.shareState -eq 'unknown' -and $null -eq $row.shareChangedAt) (J $row)

'--- write: on / off / on'
$r = Share $w $true
Check 'share:true -> 200, stato ok' ($r.Code -eq 200 -and $r.Obj.state -eq 'ok') $r.Text
$row = Row $w; $t1 = $row.shareChangedAt
Check 'dopo share:true: on, data presente' ($row.shareState -eq 'on' -and $t1) (J $row)
Check 'nessun evento alla prima comunicazione (unknown -> on)' ((Evs $w) -eq 0)
$null = Share $w $true
Check 'share:true di nuovo: nessun evento e data invariata' ((Evs $w) -eq 0 -and (Row $w).shareChangedAt -eq $t1)
Start-Sleep -Milliseconds 50
$r = Share $w $false
Check 'share:false -> 200, stato off' ($r.Code -eq 200 -and $r.Obj.state -eq 'off') $r.Text
$row = Row $w
Check 'dopo share:false: shareState off e data cambiata' ($row.shareState -eq 'off' -and $row.shareChangedAt -ne $t1) (J $row)
Check 'evento sync-setting "disattivata" scritto' ((Evs $w) -eq 1 -and (LastEv $w) -eq 'Sincronizzazione disattivata dal PC') (LastEv $w)
$t2 = $row.shareChangedAt
$null = Share $w $false
Check 'share:false di nuovo: nessun nuovo evento, data invariata' ((Evs $w) -eq 1 -and (Row $w).shareChangedAt -eq $t2)
$null = Share $w $true
Check 'share:true: torna on + evento "riattivata"' ((Row $w).shareState -eq 'on' -and (Evs $w) -eq 2 -and (LastEv $w) -eq 'Sincronizzazione riattivata dal PC') (LastEv $w)
$ev = Call GET '/api/events?kind=sync-setting' -cookies $true
Check 'registro della console: eventi sync-setting con nome del PC' ($ev.Code -eq 200 -and $ev.Obj.total -eq 2 -and $ev.Obj.items[0].pc -eq 'PC-W - User01') $ev.Text

'--- prima comunicazione "off": nessun evento'
$null = Share $w2 $false
Check 'primo contatto con share:false: off ma nessun evento' ((Row $w2).shareState -eq 'off' -and (Evs $w2) -eq 0)

'--- sola lettura: sempre on'
$r = Share $rd $false
Check 'PC read con share:false -> stato ok (preleva comunque)' ($r.Code -eq 200 -and $r.Obj.state -eq 'ok') $r.Text
Check 'PC read: shareState on e nessun evento' ((Row $rd).shareState -eq 'on' -and (Evs $rd) -eq 0)
$null = Share $rd $true; $null = Share $rd $false
Check 'PC read: resta on, nessun evento' ((Row $rd).shareState -eq 'on' -and (Evs $rd) -eq 0)

'--- requests: segue l interruttore'
$null = Share $q $false 'personal'
Check 'PC requests con un client che non conosce "requests" vale come sola lettura: sempre on' ((Row $q).shareState -eq 'on')
$null = Share $q $true 'personal,requests'; $null = Share $q $false 'personal,requests'
Check 'PC requests (client aggiornato): on poi off, un evento' ((Row $q).shareState -eq 'off' -and (Evs $q) -eq 1)

'--- non copiare deciso dall amministratore'
$r = Share $nc $true
Check 'nocopy con share:true -> stato nocopy' ($r.Code -eq 200 -and $r.Obj.state -eq 'nocopy') $r.Text
$row = Row $nc
Check 'nocopy: noCopy true e shareState on (la console mostra "bloccata")' ($row.noCopy -eq $true -and $row.shareState -eq 'on') (J $row)
$r = Share $nc $false
Check 'nocopy con share:false -> stato nocopy, shareState off + evento (e il PC che lo ha spento)' ($r.Obj.state -eq 'nocopy' -and (Row $nc).shareState -eq 'off' -and (Evs $nc) -eq 1)

'--- client senza capacita personal'
$r = Share $w $false 'shared'
Check 'senza capacita personal -> 400 e stato invariato' ($r.Code -eq 400 -and (Row $w).shareState -eq 'on' -and (Evs $w) -eq 2) $r.Text

'--- sopravvive al riavvio'
Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1
Start-Server
$null = Call POST '/api/login' (J @{ user = 'admin'; password = $adminPw }) -cookies $true
Check 'dopo il riavvio lo stato e le date sono quelli di prima' ((Row $q).shareState -eq 'off' -and (Row $w).shareState -eq 'on' -and (Row $w).shareChangedAt)

Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
"=== PASS: $script:pass   FAIL: $script:fail"
