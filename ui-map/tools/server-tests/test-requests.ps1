# Prova delle "richieste" (permesso requests) e delle capacita' del client (X-Lemora-Caps) sul server vero: processo a parte, cartella dati usa e getta.
# Esito: PASS/FAIL per ogni controllo.  Uso: .\test-requests.ps1 [-Port 18611] [-Exe <Lemora.Server.exe>]
param([int]$Port = 18611, [string]$Exe = 'C:\_dev\_projects\_repos\1Remote\Server\bin\Release\net10.0\Lemora.Server.exe')
$work = Join-Path $env:TEMP ('pairing-test-' + [guid]::NewGuid().ToString('N')) # stesso prefisso delle altre prove: cleanup-tests.ps1 la ripulisce
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$dbf = Join-Path $db 'server.db'; $helper = Join-Path $PSScriptRoot 'conn-db.py'
$base = "https://127.0.0.1:$Port"
$adminPw = 'Password-di-prova-123'
$CAPS = 'vnc,requests'
$script:pass = 0; $script:fail = 0

function Call($method, $path, $json = $null, [string]$bearer = '', [bool]$cookies = $false, [string]$caps = '') {
    $a = @('-k', '-s', '-X', $method, '-w', '\n%{http_code}', '-H', 'X-Lemora: 1')
    if ($cookies) { $a += @('-b', $jar, '-c', $jar) }
    if ($bearer) { $a += @('-H', "Authorization: Bearer $bearer") }
    if ($caps) { $a += @('-H', "X-Lemora-Caps: $caps") }
    if ($json -ne $null) { Set-Content -Path $body -Value $json -NoNewline -Encoding ascii; $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$body") }
    $a += "$base$path"
    $out = (& curl.exe @a) -join "`n"
    $i = $out.LastIndexOf("`n")
    $text = if ($i -ge 0) { $out.Substring(0, $i) } else { '' }
    $obj = $null; try { if ($text) { $obj = $text | ConvertFrom-Json } } catch { }
    [pscustomobject]@{ Code = [int]$out.Substring($i + 1); Obj = $obj; Text = $text }
}
function Check($label, $cond, $detail = '') { if ($cond) { $script:pass++; "PASS  $label" } else { $script:fail++; "FAIL  $label   $detail" } }
function Admin($method, $path, $json = $null) { Call $method $path $json '' $true }
function Py { (& python -I $helper @args) -join '' }
function Conn($id) { @((Admin GET '/api/connections').Obj) | Where-Object { $_.id -eq $id } | Select-Object -First 1 }
function Req($id) { @((Admin GET '/api/requests').Obj) | Where-Object { $_.id -eq $id } | Select-Object -First 1 }
function Reveal($id) { (Admin POST "/api/connections/$id/reveal" '{}').Obj.password }
function Ask($key, [hashtable]$b, [string]$caps = $CAPS) { Call POST '/api/client/requests' ($b | ConvertTo-Json -Compress) $key $false $caps }
function New-Pc($name, $perm) {
    $c = (Admin POST '/api/pairing/new' '{}').Obj.code
    $r = Call POST '/api/pair/request' (@{ code = $c; pc = $name; user = 'User01' } | ConvertTo-Json -Compress)
    $null = Admin POST "/api/pcs/$($r.Obj.id)/approve" (@{ permission = $perm } | ConvertTo-Json -Compress)
    $s = Call POST '/api/pair/status' (@{ id = $r.Obj.id; token = $r.Obj.token } | ConvertTo-Json -Compress) '' $false $CAPS
    [pscustomobject]@{ Id = $r.Obj.id; Key = $s.Obj.key; Perm = $s.Obj.permission }
}

$proc = Start-Process $Exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'out.txt') -RedirectStandardError (Join-Path $work 'err.txt')
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Call GET '/api/state').Code -eq 200) { break } }
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
$null = Admin POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress)
$r = Admin POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = $adminPw; port = $Port; address = 'all' } | ConvertTo-Json -Compress)
Check 'configurazione' ($r.Code -eq 200) $r.Text
Start-Sleep 1
$null = Admin POST '/api/login' (@{ user = 'admin'; password = $adminPw } | ConvertTo-Json -Compress)

$idA = (Admin POST '/api/connections' '{"folder":"Roma","name":"Voce A","protocol":"rdp","host":"10.0.0.1","port":3389,"user":"admin","password":"pw-a","tags":"web"}').Obj.id
$idB = (Admin POST '/api/connections' '{"folder":"","name":"Voce B","protocol":"ssh","host":"10.0.0.2","port":22,"user":"root","password":"pw-b","tags":""}').Obj.id
$idV = (Admin POST '/api/connections' '{"folder":"","name":"Voce VNC","protocol":"vnc","host":"10.0.0.3","port":5900,"user":"","password":"pw-v","tags":""}').Obj.id
$idX = (Admin POST '/api/connections' '{"folder":"","name":"Voce X","protocol":"rdp","host":"10.0.0.4","port":3389,"user":"","password":"","tags":""}').Obj.id
Check '4 voci create (una VNC)' ($idA -and $idB -and $idV -and $idX)

'--- permessi e capacita'' del client'
$pr = New-Pc 'PC-RICHIESTE' 'requests'; $pw = New-Pc 'PC-SCRITTURA' 'write'; $pd = New-Pc 'PC-LETTURA' 'read'
Check 'console: il PC ha il permesso requests' ((@((Admin GET '/api/pcs').Obj) | Where-Object { $_.id -eq $pr.Id }).permission -eq 'requests')
Check 'pair/status con capacita'': permesso requests' ($pr.Perm -eq 'requests') $pr.Perm
$r = Call GET '/api/client/me' $null $pr.Key $false $CAPS
Check 'client/me con capacita'': requests' ($r.Obj.permission -eq 'requests') $r.Text
$r = Call GET '/api/client/me' $null $pr.Key
Check 'client/me SENZA capacita'' (client vecchio): requests letto come read' ($r.Obj.permission -eq 'read') $r.Text
$r = Call GET '/api/client/sync?since=0' $null $pr.Key $false $CAPS
Check 'sync con capacita'' vnc: 4 voci, VNC compresa, permesso requests' (@($r.Obj.items).Count -eq 4 -and (@($r.Obj.items) | Where-Object { $_.protocol -eq 'vnc' }) -and $r.Obj.permission -eq 'requests') $r.Text
$rev = $r.Obj.rev
$r = Call GET '/api/client/sync?since=0' $null $pd.Key
Check 'sync SENZA capacita'' da zero: 3 voci, nessuna VNC' (@($r.Obj.items).Count -eq 3 -and -not ($r.Text -match '"vnc"')) $r.Text
$r = Call GET "/api/client/sync?since=$rev" $null $pd.Key
$t = @($r.Obj.items)
Check 'sync SENZA capacita'' incrementale: la VNC arriva solo come cancellata (la toglie dalla copia vecchia)' ($t.Count -eq 1 -and $t[0].id -eq $idV -and $t[0].deleted -eq $true -and $t[0].password -eq $null -and $t[0].host -eq '') $r.Text
Check 'registro: le voci VNC tolte non contano come scaricate (1 sola riga sync per il PC lettura)' ([int](Py events $dbf 'sync' $pd.Id) -eq 1)
$r = Call GET '/api/client/sync?since=0' $null $pr.Key
Check 'sync SENZA capacita'' del PC requests: permesso read' ($r.Obj.permission -eq 'read') $r.Text

'--- PC aggiornato (o riportato indietro): capacita'' cambiate = sync completa, senza toccare le voci'
$pu = New-Pc 'PC-AGGIORNATO' 'read'
$r = Call GET '/api/client/sync?since=0' $null $pu.Key
$ru = $r.Obj.rev
$r = Call GET "/api/client/sync?since=$ru" $null $pu.Key
Check 'client vecchio: VNC cancellata, cursore avanti' (@($r.Obj.items).Count -eq 1 -and @($r.Obj.items)[0].deleted -eq $true -and $r.Obj.rev -eq $ru) $r.Text
$r = Call GET "/api/client/sync?since=$ru" $null $pu.Key $false $CAPS
$v = @($r.Obj.items) | Where-Object { $_.id -eq $idV }
Check 'stesso PC aggiornato (capacita'' vnc), stesso cursore: la VNC ricompare' ($v -and $v.deleted -eq $false -and $v.host -eq '10.0.0.3' -and $v.password -eq 'pw-v' -and @($r.Obj.items).Count -eq 4) $r.Text
Check '...senza modificare la voce sul server (versione 1)' ((Conn $idV).version -eq 1)
$r = Call GET "/api/client/sync?since=$($r.Obj.rev)" $null $pu.Key $false $CAPS
Check 'sync dopo con le stesse capacita'': di nuovo incrementale (nessuna voce)' (@($r.Obj.items).Count -eq 0) $r.Text
$r = Call GET "/api/client/sync?since=$($r.Obj.rev)" $null $pu.Key
$t = @($r.Obj.items)
Check 'riportato a client vecchio: la VNC arriva cancellata, nessuna VNC visibile' (($t | Where-Object { $_.id -eq $idV }).deleted -eq $true -and -not ($t | Where-Object { $_.protocol -eq 'vnc' -and -not $_.deleted })) $r.Text

'--- chi puo'' mandare richieste'
$upd = @{ kind = 'update'; connId = $idA; baseVersion = 1; folder = 'Roma'; name = 'Voce A'; protocol = 'rdp'; host = '10.0.0.111'; port = 3390; user = 'admin'; password = 'pw-a'; tags = 'web' }
$r = Ask $pd.Key $upd
Check 'PC in sola lettura -> 403' ($r.Code -eq 403 -and $r.Obj.error -eq 'permission') $r.Text
$r = Ask $pw.Key $upd
Check 'PC con scrittura -> 403 (scrive direttamente)' ($r.Code -eq 403) $r.Text
$r = Ask $pr.Key $upd ''
Check 'PC requests ma client vecchio (senza capacita'') -> 403' ($r.Code -eq 403) $r.Text
$r = Call POST "/api/client/connections/$idA/update" ($upd | ConvertTo-Json -Compress) $pr.Key $false $CAPS
Check 'PC requests: la scrittura diretta resta vietata (403)' ($r.Code -eq 403) $r.Text
$r = Call POST '/api/client/requests' '{}' ''
Check 'senza chiave -> 401' ($r.Code -eq 401) $r.Text

'--- dati sbagliati'
$r = Ask $pr.Key @{ kind = 'boh'; connId = $idA }
Check 'tipo sconosciuto -> 400' ($r.Code -eq 400) $r.Text
$r = Ask $pr.Key @{ kind = 'delete'; connId = ('0' * 32) }
Check 'voce inesistente -> 404' ($r.Code -eq 404) $r.Text
$bad = $upd.Clone(); $bad.port = 0
$r = Ask $pr.Key $bad
Check 'porta 0 -> 400 campo port' ($r.Code -eq 400 -and $r.Obj.field -eq 'port') $r.Text

'--- richieste in attesa'
$r = Ask $pr.Key $upd
Check 'richiesta di modifica -> 200 pending' ($r.Code -eq 200 -and $r.Obj.state -eq 'pending' -and $r.Obj.id.Length -eq 32) $r.Text
$qA = $r.Obj.id
$updB = @{ kind = 'update'; connId = $idB; baseVersion = 1; folder = ''; name = 'Voce B'; protocol = 'ssh'; host = '10.0.0.2'; port = 22; user = 'root'; password = 'pw-b-NUOVA'; tags = '' }
$qB = (Ask $pr.Key $updB).Obj.id
$qD = (Ask $pr.Key @{ kind = 'delete'; connId = $idB; baseVersion = 1 }).Obj.id
$qC = (Ask $pr.Key @{ kind = 'create'; folder = 'Nuove'; name = 'Voce chiesta'; protocol = 'rdp'; host = '10.0.0.9'; port = 3389; user = 'u'; password = 'pw-c'; tags = '' }).Obj.id
Check 'richieste di modifica (password), eliminazione e nuova voce accettate' ($qB -and $qD -and $qC)
$a = Conn $idA
Check 'la voce NON cambia finche'' l''admin non approva' ($a.host -eq '10.0.0.1' -and $a.version -eq 1) ($a | ConvertTo-Json -Compress)
$list = Admin GET '/api/requests'
Check 'console: 4 richieste in attesa' (@($list.Obj | Where-Object { $_.state -eq 'pending' }).Count -eq 4) $list.Text
Check 'console: nessuna password in chiaro nell''elenco' (-not ($list.Text -match 'pw-a|pw-b|pw-c') -and $list.Text -notmatch '"password"') $list.Text
$x = Req $qA
Check 'console: vecchio e nuovo valore, PC - utente, password invariata' ($x.old.host -eq '10.0.0.1' -and $x.new.host -eq '10.0.0.111' -and $x.new.port -eq 3390 -and $x.pc -eq 'PC-RICHIESTE - User01' -and $x.pwChanged -eq $false -and $x.kind -eq 'update') ($x | ConvertTo-Json -Compress -Depth 4)
Check 'console: password cambiata segnalata (senza valore)' ((Req $qB).pwChanged -eq $true)
Check 'console: eliminazione con i valori attuali, senza nuovi' ((Req $qD).old.name -eq 'Voce B' -and (Req $qD).new -eq $null)
Check 'console: nuova voce senza valori vecchi' ((Req $qC).old -eq $null -and (Req $qC).new.name -eq 'Voce chiesta')
$r = Call GET "/api/client/sync?since=$rev" $null $pr.Key $false $CAPS
Check 'sync del PC: le sue 4 richieste in attesa' (@($r.Obj.requests | Where-Object { $_.state -eq 'pending' }).Count -eq 4) $r.Text
$r = Call GET "/api/client/sync?since=$rev" $null $pw.Key $false $CAPS
Check 'un altro PC non vede le richieste altrui' (@($r.Obj.requests).Count -eq 0) $r.Text
Check 'registro: 4 request-created del PC' ([int](Py events $dbf 'request-created' $pr.Id) -eq 4)

'--- rifiuto'
$r = Admin POST "/api/requests/$qB/reject" '{"note":"password gestita da IT"}'
Check 'rifiuta con nota' ($r.Code -eq 200) $r.Text
Check 'rifiuto: la voce non cambia (password originale)' ((Reveal $idB) -eq 'pw-b' -and (Conn $idB).version -eq 1)
$x = Req $qB
Check 'storico: rifiutata con nota e data' ($x.state -eq 'rejected' -and $x.note -eq 'password gestita da IT' -and $x.decidedAt) ($x | ConvertTo-Json -Compress -Depth 4)
$r = Call GET "/api/client/sync?since=$rev" $null $pr.Key $false $CAPS
$m = @($r.Obj.requests) | Where-Object { $_.id -eq $qB }
Check 'il PC riceve l''esito con la nota' ($m.state -eq 'rejected' -and $m.note -eq 'password gestita da IT') $r.Text
$r = Admin POST "/api/requests/$qB/approve" '{}'
Check 'decidere due volte -> 409 state' ($r.Code -eq 409 -and $r.Obj.error -eq 'state') $r.Text

'--- approvazione'
$r = Admin POST "/api/requests/$qA/approve" '{}'
Check 'approva la modifica' ($r.Code -eq 200) $r.Text
$a = Conn $idA
Check 'modifica applicata: host, porta, versione 2, password tenuta' ($a.host -eq '10.0.0.111' -and $a.port -eq 3390 -and $a.version -eq 2 -and (Reveal $idA) -eq 'pw-a') ($a | ConvertTo-Json -Compress)
$r = Call GET "/api/client/sync?since=$rev" $null $pr.Key $false $CAPS
Check 'il PC riceve la voce nuova e l''esito approved' ((@($r.Obj.items) | Where-Object { $_.id -eq $idA }).host -eq '10.0.0.111' -and (@($r.Obj.requests) | Where-Object { $_.id -eq $qA }).state -eq 'approved') $r.Text
$r = Admin POST "/api/requests/$qC/approve" '{}'
$new = @((Admin GET '/api/connections').Obj) | Where-Object { $_.name -eq 'Voce chiesta' }
Check 'approva la nuova voce: creata, cartella, password cifrata giusta' ($r.Code -eq 200 -and $new.folder -eq 'Nuove' -and (Reveal $new.id) -eq 'pw-c') $r.Text
Check 'la richiesta approvata punta alla voce creata' ((Req $qC).connId -eq $new.id)
$r = Admin POST "/api/requests/$qD/approve" '{}'
Check 'approva l''eliminazione: la voce va nel cestino' ($r.Code -eq 200 -and -not (Conn $idB)) $r.Text
$r = Call GET "/api/client/sync?since=$rev" $null $pw.Key $false $CAPS
Check 'gli altri PC ricevono l''eliminazione' ((@($r.Obj.items) | Where-Object { $_.id -eq $idB }).deleted -eq $true) $r.Text

'--- voce cambiata dopo la richiesta (stale)'
$upd2 = $upd.Clone(); $upd2.baseVersion = 2; $upd2.host = '10.0.0.222'
$qS = (Ask $pr.Key $upd2).Obj.id
Check 'richiesta sulla versione attuale: pending' ((Req $qS).state -eq 'pending')
$null = Admin POST "/api/connections/$idA/update" '{"folder":"Roma","name":"Voce A","protocol":"rdp","host":"10.0.0.150","port":3390,"user":"admin","password":null,"tags":"web"}'
Check 'dopo una modifica dell''admin la richiesta diventa stale' ((Req $qS).state -eq 'stale')
$r = Admin POST "/api/requests/$qS/approve" '{}'
$a = Conn $idA
Check 'approvare una stale sovrascrive (host richiesto, versione 4)' ($r.Code -eq 200 -and $a.host -eq '10.0.0.222' -and $a.version -eq 4) ($a | ConvertTo-Json -Compress)
$qG = (Ask $pr.Key @{ kind = 'update'; connId = $idX; baseVersion = 1; folder = ''; name = 'Voce X'; protocol = 'rdp'; host = '10.0.0.44'; port = 3389; user = ''; password = ''; tags = '' }).Obj.id
$null = Admin POST "/api/connections/$idX/delete" '{}'
Check 'voce eliminata dopo la richiesta: stale e senza valori attuali' ((Req $qG).state -eq 'stale' -and (Req $qG).old -eq $null)
$r = Admin POST "/api/requests/$qG/approve" '{}'
Check 'approvare la modifica di una voce eliminata -> 409 notfound' ($r.Code -eq 409 -and $r.Obj.error -eq 'notfound') $r.Text
$r = Admin POST "/api/requests/$qG/reject" '{}'
Check '...ma si puo'' rifiutare' ($r.Code -eq 200 -and (Req $qG).state -eq 'rejected') $r.Text

'--- registro e permesso cambiato'
Check 'registro: request-approved (4) e request-rejected (2)' ([int](Py events $dbf 'request-approved' $pr.Id) -eq 4 -and [int](Py events $dbf 'request-rejected' $pr.Id) -eq 2)
$null = Admin POST "/api/pcs/$($pr.Id)/update" '{"permission":"read"}'
$r = Ask $pr.Key $upd
Check 'passato a sola lettura: le richieste -> 403' ($r.Code -eq 403) $r.Text
$r = Admin POST '/api/requests/xyz/approve' '{}'
Check 'richiesta inesistente -> 409 notfound' ($r.Code -eq 409) $r.Text
$r = Call GET '/api/requests'
Check 'elenco richieste senza accesso -> 401' ($r.Code -eq 401) $r.Text

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { } # Remove-Item non accetta il percorso breve di TEMP (I7208~1)
"=== PASS: $script:pass   FAIL: $script:fail"
