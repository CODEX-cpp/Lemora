# Prova delle connessioni personali dei PC (/api/client/personal, pagina "Proposte") sul server vero: processo a parte, cartella dati usa e getta.
# Regola: TEAMS-DESIGN.md sezione 3 "Condivisione delle connessioni personali del PC".
# Esito: PASS/FAIL per ogni controllo.  Uso: .\test-personal.ps1 [-Port 18631] [-Exe <Lemora.Server.exe>]
param([int]$Port = 18631, [string]$Exe = 'C:\_dev\_projects\_repos\1Remote\Server\bin\Release\net10.0\Lemora.Server.exe')
$work = Join-Path $env:TEMP ('pairing-test-' + [guid]::NewGuid().ToString('N')) # stesso prefisso delle altre prove: cleanup-tests.ps1 la ripulisce
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$dbf = Join-Path $db 'server.db'; $helper = Join-Path $PSScriptRoot 'conn-db.py'
$base = "https://127.0.0.1:$Port"
$adminPw = 'Password-di-prova-123'
$CAPS = 'vnc,requests,personal'
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
function Conns { @((Admin GET '/api/connections').Obj) }
function PersList { @((Admin GET '/api/personal').Obj) }
function Row($pcId, $lid) { PersList | Where-Object { $_.pcId -eq $pcId -and $_.localId -eq $lid } | Select-Object -First 1 }
function Reveal($id) { (Admin POST "/api/connections/$id/reveal" '{}').Obj.password }
function It($lid, $name, $h, $port = 3389, $user = 'admin', $pw = '', $proto = 'rdp', $folder = 'Uff') {
    @{ localId = $lid; folder = $folder; name = $name; protocol = $proto; host = $h; port = $port; user = $user; password = $pw; tags = '' }
}
function Send($key, [bool]$share, $items = @(), $removed = @(), [string]$caps = $CAPS) {
    $j = @{ share = $share; items = @($items); removed = @($removed) } | ConvertTo-Json -Compress -Depth 4
    Call POST '/api/client/personal' $j $key $false $caps
}
function St($r, $lid) { (@($r.Obj.items) | Where-Object { $_.localId -eq $lid }).state }
function Sync($key, $since = 0, [string]$caps = $CAPS) { Call GET "/api/client/sync?since=$since" $null $key $false $caps }
function New-Pc($name, $perm, [bool]$noCopy = $false) {
    $c = (Admin POST '/api/pairing/new' '{}').Obj.code
    $r = Call POST '/api/pair/request' (@{ code = $c; pc = $name; user = 'User01' } | ConvertTo-Json -Compress)
    $null = Admin POST "/api/pcs/$($r.Obj.id)/approve" (@{ permission = $perm; noCopy = $noCopy } | ConvertTo-Json -Compress)
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

$pd = New-Pc 'PC-LETTURA' 'read'; $pn = New-Pc 'PC-NOCOPY' 'read' $true; $pr = New-Pc 'PC-RICHIESTE' 'requests'; $pw = New-Pc 'PC-SCRITTURA' 'write'; $po = New-Pc 'PC-ALTRO' 'read'
$idS = (Admin POST '/api/connections' '{"folder":"","name":"Gia condivisa","protocol":"rdp","host":"10.9.9.9","port":3389,"user":"admin","password":"x","tags":""}').Obj.id
Check '5 PC abbinati e una voce condivisa' ($pd.Key -and $pn.Key -and $pr.Key -and $pw.Key -and $po.Key -and $idS)

'--- client vecchio (senza capacita'' personal)'
$r = Send $pd.Key $true @(It 'L1' 'Lett 1' '10.1.1.1' 3389 'admin' 'pwd-L1-secret') @() 'vnc,requests'
Check 'senza capacita'' -> 400 caps, nulla salvato' ($r.Code -eq 400 -and $r.Obj.error -eq 'caps' -and @(PersList).Count -eq 0) $r.Text
$r = Sync $pd.Key 0 'vnc,requests'
Check 'sync senza capacita'': personal assente (null)' ($r.Code -eq 200 -and $r.Obj.personal -eq $null) $r.Text
$r = Call POST '/api/client/personal' '{"share":true,"items":[]}'
Check 'senza chiave -> 401' ($r.Code -eq 401) $r.Text

'--- sola lettura: raccolte sempre, in attesa'
$r = Send $pd.Key $false @((It 'L1' 'Lett 1' '10.1.1.1' 3389 'admin' 'pwd-L1-secret'), (It 'L2' 'Lett 2' '10.1.1.2' 22 'root' 'pwd-L2-secret' 'ssh'), (It 'L3' 'Lett 3' '10.1.1.3' 5900 '' 'pwd-L3-secret' 'vnc'))
Check 'read (share=false ignorato): 200 ok, 3 voci pending' ($r.Code -eq 200 -and $r.Obj.state -eq 'ok' -and (St $r 'L1') -eq 'pending' -and (St $r 'L3') -eq 'pending' -and @($r.Obj.items).Count -eq 3) $r.Text
$x = Row $pd.Id 'L1'
Check 'console: proposta con PC - utente, indirizzo, password nascosta' ($x.state -eq 'pending' -and $x.pc -eq 'PC-LETTURA - User01' -and $x.host -eq '10.1.1.1' -and $x.hasPassword -eq $true -and -not $x.PSObject.Properties['password']) ($x | ConvertTo-Json -Compress -Depth 4)
Check 'non ancora condivise: nessuna voce nuova' (@(Conns).Count -eq 1)
$r = Send $pd.Key $false @(It 'BAD' '' '10.1.1.9')
Check 'voce non valida (nome vuoto) -> stato invalid, non salvata' ((St $r 'BAD') -eq 'invalid' -and -not (Row $pd.Id 'BAD')) $r.Text

'--- non copiare'
$r = Send $pn.Key $true @(It 'N1' 'NoCopy 1' '10.2.2.1' 3389 'u' 'pwd-N1-secret')
Check 'PC con "non copiare": 200 nocopy, nulla salvato' ($r.Code -eq 200 -and $r.Obj.state -eq 'nocopy' -and -not (PersList | Where-Object { $_.pcId -eq $pn.Id })) $r.Text

'--- richieste'
$r = Send $pr.Key $false @(It 'R1' 'Rich 1' '10.3.3.1' 3389 'admin' 'pwd-R1-secret')
Check 'requests con interruttore spento: off, nulla salvato' ($r.Obj.state -eq 'off' -and -not (Row $pr.Id 'R1')) $r.Text
$r = Send $pr.Key $true @((It 'R1' 'Rich 1' '10.3.3.1' 3389 'admin' 'pwd-R1-secret'), (It 'R2' 'Rich 2' '10.3.3.2' 3389 'admin' 'pwd-R2-secret'), (It 'R3' 'Doppione' '10.9.9.9' 3389 'ADMIN' 'pwd-R3-secret'))
Check 'requests con interruttore acceso: pending' ((St $r 'R1') -eq 'pending' -and (Row $pr.Id 'R1').state -eq 'pending') $r.Text
$r = Send $pr.Key $true @(It 'R9' 'Rich 9' '10.3.3.9' 3389 'admin' 'pwd-R9-secret') @() 'vnc,personal'
Check 'requests ma client senza capacita'' requests = read: accettata (pending)' ((St $r 'R9') -eq 'pending') $r.Text
$null = Send $pr.Key $false
Check 'requests: interruttore spento dopo = copie non pubblicate cancellate' (-not (PersList | Where-Object { $_.pcId -eq $pr.Id })) ((PersList | ConvertTo-Json -Compress -Depth 4))
$r = Send $pr.Key $true @((It 'R1' 'Rich 1' '10.3.3.1' 3389 'admin' 'pwd-R1-secret'), (It 'R2' 'Rich 2' '10.3.3.2' 3389 'admin' 'pwd-R2-secret'), (It 'R3' 'Doppione' '10.9.9.9' 3389 'ADMIN' 'pwd-R3-secret'))
Check '...riaccesa: di nuovo pending' (@($r.Obj.items | Where-Object { $_.state -eq 'pending' }).Count -eq 3) $r.Text

'--- scrittura: pubblica subito'
$r = Send $pw.Key $false @(It 'W1' 'Scrit 1' '10.4.4.1' 3389 'admin' 'pwd-W1-secret')
Check 'write con interruttore spento: off' ($r.Obj.state -eq 'off' -and -not (Row $pw.Id 'W1')) $r.Text
$r = Send $pw.Key $true @(It 'W1' 'Scrit 1' '10.4.4.1' 3389 'admin' 'pwd-W1-secret')
$w1 = (@($r.Obj.items) | Where-Object { $_.localId -eq 'W1' }).connId
Check 'write con interruttore acceso: published subito con id della voce' ((St $r 'W1') -eq 'published' -and $w1.Length -eq 32) $r.Text
$c = Conns | Where-Object { $_.id -eq $w1 }
Check 'voce condivisa creata (versione 1, password cifrata giusta)' ($c.name -eq 'Scrit 1' -and $c.version -eq 1 -and (Reveal $w1) -eq 'pwd-W1-secret') ($c | ConvertTo-Json -Compress)
$r = Sync $po.Key
$v = @($r.Obj.items) | Where-Object { $_.id -eq $w1 }
Check 'un altro PC la riceve subito, con origin {pc, localId}' ($v -and $v.origin.pc -eq $pw.Id -and $v.origin.localId -eq 'W1' -and $v.password -eq 'pwd-W1-secret') $r.Text
Check 'voce dell''admin senza origin' ((@($r.Obj.items) | Where-Object { $_.id -eq $idS }).origin -eq $null) $r.Text

'--- doppioni'
$x = Row $pr.Id 'R3'
Check 'doppione di una voce condivisa (host+porta+utente, maiuscole ignorate)' (@($x.dups | Where-Object { $_.kind -eq 'shared' -and $_.name -eq 'Gia condivisa' }).Count -eq 1) ($x | ConvertTo-Json -Compress -Depth 4)
$null = Send $po.Key $false @(It 'O1' 'Altro 1' '10.3.3.1' 3389 'admin' 'pwd-O1-secret')
$x = Row $po.Id 'O1'
Check 'doppione di una proposta di un altro PC (con il nome del PC)' (@($x.dups | Where-Object { $_.kind -eq 'proposal' -and $_.pc -eq 'PC-RICHIESTE - User01' }).Count -eq 1) ($x | ConvertTo-Json -Compress -Depth 4)
Check '...senza unirle: le due proposte restano separate' ((Row $pr.Id 'R1').state -eq 'pending' -and (Row $po.Id 'O1').state -eq 'pending')

'--- approva e rifiuta'
$x = Row $pd.Id 'L1'
$r = Admin POST "/api/personal/$($x.id)/approve" '{"note":"ok per tutti"}'
$x = Row $pd.Id 'L1'
Check 'approva: pubblicata con la voce creata' ($r.Code -eq 200 -and $x.state -eq 'published' -and $x.connId.Length -eq 32 -and $x.note -eq 'ok per tutti') $r.Text
$c = Conns | Where-Object { $_.id -eq $x.connId }
Check 'voce condivisa per tutti: versione 1, cartella, password giusta' ($c.version -eq 1 -and $c.folder -eq 'Uff' -and (Reveal $c.id) -eq 'pwd-L1-secret') ($c | ConvertTo-Json -Compress)
$r = Sync $pd.Key
$v = @($r.Obj.items) | Where-Object { $_.id -eq $x.connId }
$m = @($r.Obj.personal) | Where-Object { $_.localId -eq 'L1' }
Check 'sync del PC d''origine: origin = questo PC + L1, stato published con nota' ($v.origin.pc -eq $pd.Id -and $v.origin.localId -eq 'L1' -and $m.state -eq 'published' -and $m.note -eq 'ok per tutti') $r.Text
Check 'sync: L2 ancora pending' ((@($r.Obj.personal) | Where-Object { $_.localId -eq 'L2' }).state -eq 'pending') $r.Text
$r = Admin POST "/api/personal/$($x.id)/approve" '{}'
Check 'decidere due volte -> 409 state' ($r.Code -eq 409 -and $r.Obj.error -eq 'state') $r.Text
$x = Row $pr.Id 'R2'
$r = Admin POST "/api/personal/$($x.id)/reject" '{"note":"usa la voce Uffici"}'
Check 'rifiuta con nota: resta personale, nessuna voce nuova' ($r.Code -eq 200 -and (Row $pr.Id 'R2').state -eq 'rejected' -and -not (Conns | Where-Object { $_.host -eq '10.3.3.2' })) $r.Text
$r = Sync $pr.Key
$m = @($r.Obj.personal) | Where-Object { $_.localId -eq 'R2' }
Check 'il PC riceve il rifiuto con la nota' ($m.state -eq 'rejected' -and $m.note -eq 'usa la voce Uffici') $r.Text
$r = Send $pr.Key $true @(It 'R2' 'Rich 2' '10.3.3.2' 3389 'admin' 'pwd-R2-secret')
Check 'rifiutata reinviata uguale: resta rifiutata' ((St $r 'R2') -eq 'rejected') $r.Text

'--- rifiuto che resta (voce 112): permesso scrittura, interruttore spento/acceso, nuovo abbinamento'
$null = Admin POST "/api/pcs/$($pr.Id)/update" '{"permission":"write","noCopy":false}'
$r = Send $pr.Key $false
Check 'PC passato a scrittura, interruttore spento: off, righe non pubblicate cancellate' ($r.Obj.state -eq 'off' -and -not (Row $pr.Id 'R2')) $r.Text
$r = Send $pr.Key $true @(It 'R2' 'Rich 2' '10.3.3.2' 3389 'admin' 'pwd-R2-secret')
Check '...riacceso con la stessa voce: resta rejected, NON condivisa' ((St $r 'R2') -eq 'rejected' -and -not (Conns | Where-Object { $_.host -eq '10.3.3.2' })) $r.Text
$r = Sync $pr.Key
$m = @($r.Obj.personal) | Where-Object { $_.localId -eq 'R2' }
Check '...in console e al PC resta rifiutata con la nota di allora' ((Row $pr.Id 'R2').state -eq 'rejected' -and $m.state -eq 'rejected' -and $m.note -eq 'usa la voce Uffici') $r.Text
$p2 = New-Pc 'PC-RICHIESTE' 'write'
$r = Send $p2.Key $true @(It 'R2' 'Rich 2' '10.3.3.2' 3389 'admin' 'pwd-R2-secret')
Check 'nuovo abbinamento (scrittura), stessa voce: resta rejected' ((St $r 'R2') -eq 'rejected' -and -not (Conns | Where-Object { $_.host -eq '10.3.3.2' })) $r.Text
$r = Send $pr.Key $true @(It 'R2' 'Rich 2' '10.3.3.2' 3389 'admin' 'pwd-R2-cambiata')
$c = Conns | Where-Object { $_.host -eq '10.3.3.2' }
Check 'voce modificata (password): pubblicata' ((St $r 'R2') -eq 'published' -and @($c).Count -eq 1 -and (Reveal $c.id) -eq 'pwd-R2-cambiata') $r.Text
$r = Admin POST "/api/personal/pc/$($pd.Id)/approve-all" '{}'
Check 'approva tutte del PC lettura: 2 (L2, L3)' ($r.Code -eq 200 -and $r.Obj.approved -eq 2 -and (Row $pd.Id 'L3').state -eq 'published') $r.Text
$r = Admin POST '/api/personal/xyz/approve' '{}'
Check 'proposta inesistente -> 409 notfound' ($r.Code -eq 409) $r.Text
$r = Call GET '/api/personal'
Check 'elenco proposte senza accesso -> 401' ($r.Code -eq 401) $r.Text

'--- aggiornamenti'
$ev = [int](Py events $dbf 'personal-received' $po.Id)
$r = Send $po.Key $false @(It 'O1' 'Altro 1' '10.3.3.1' 3389 'admin' 'pwd-O1-secret')
Check 'reinvio identico: nessun evento nuovo' ([int](Py events $dbf 'personal-received' $po.Id) -eq $ev -and (St $r 'O1') -eq 'pending')
$r = Send $po.Key $false @(It 'O1' 'Altro 1' '10.3.3.111' 3389 'admin' 'pwd-O1-secret')
Check 'aggiornamento di una pending: copia aggiornata' ((Row $po.Id 'O1').host -eq '10.3.3.111' -and [int](Py events $dbf 'personal-received' $po.Id) -eq $ev + 1)
$r = Send $pw.Key $true @(It 'W1' 'Scrit 1' '10.4.4.11' 3390 'admin' 'pwd-W1-nuova')
$c = Conns | Where-Object { $_.id -eq $w1 }
Check 'write: aggiornamento della pubblicata = voce condivisa aggiornata (versione 2)' ((St $r 'W1') -eq 'published' -and $c.host -eq '10.4.4.11' -and $c.version -eq 2 -and (Reveal $w1) -eq 'pwd-W1-nuova') ($c | ConvertTo-Json -Compress)
$null = Admin POST "/api/connections/$w1/update" '{"folder":"Uff","name":"Scrit 1","protocol":"rdp","host":"10.4.4.12","port":3390,"user":"admin","password":null,"tags":""}'
$r = Send $pw.Key $true @(It 'W1' 'Scrit 1' '10.4.4.13' 3390 'admin' 'pwd-W1-nuova')
Check 'write: voce cambiata dall''admin nel frattempo -> conflict, server invariato' ((St $r 'W1') -eq 'conflict' -and (Conns | Where-Object { $_.id -eq $w1 }).host -eq '10.4.4.12') $r.Text
$l1 = (Row $pd.Id 'L1').connId
$r = Send $pd.Key $false @(It 'L1' 'Lett 1' '10.1.1.99' 3389 'admin' 'pwd-L1-secret')
Check 'read: aggiornamento di una pubblicata ignorato (la voce e'' del team)' ((St $r 'L1') -eq 'published' -and (Conns | Where-Object { $_.id -eq $l1 }).host -eq '10.1.1.1') $r.Text

'--- eliminate sul PC (removed)'
$null = Send $po.Key $false @(It 'O2' 'Altro 2' '10.5.5.2')
$r = Send $po.Key $false @() @('O2')
Check 'removed: la proposta in attesa sparisce' ($r.Code -eq 200 -and -not (Row $po.Id 'O2'))
$r = Send $pd.Key $false @() @('L1')
Check 'removed di una pubblicata: la voce condivisa resta' ((Row $pd.Id 'L1').state -eq 'published' -and (Conns | Where-Object { $_.id -eq $l1 }))
$null = Admin POST "/api/pcs/$($po.Id)/update" '{"permission":"read","noCopy":true}'
$r = Send $po.Key $false @(It 'O3' 'Altro 3' '10.5.5.3')
Check '"non copiare" acceso dopo: nocopy e copie in attesa cancellate' ($r.Obj.state -eq 'nocopy' -and -not (PersList | Where-Object { $_.pcId -eq $po.Id })) $r.Text

'--- registro e password'
Check 'registro: personal-received del PC lettura (3)' ([int](Py events $dbf 'personal-received' $pd.Id) -eq 3)
Check 'registro: personal-approved (3) e personal-published (3 lettura, 2 scrittura)' ([int](Py events $dbf 'personal-approved' $pd.Id) -eq 3 -and [int](Py events $dbf 'personal-published' $pd.Id) -eq 3 -and [int](Py events $dbf 'personal-published' $pw.Id) -eq 2)
Check 'registro: personal-rejected (1) del PC richieste' ([int](Py events $dbf 'personal-rejected' $pr.Id) -eq 1)
$all = (Admin GET '/api/personal').Text
Check 'console: nessuna password in chiaro' (-not ($all -match 'secret|pwd-W1') -and $all -notmatch '"password"') $all
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
$raw = ''
foreach ($f in Get-ChildItem $db -File) { $raw += [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($f.FullName)) }
Check 'database: nessuna password in chiaro (file server.db e annessi)' ($raw.Length -gt 0 -and $raw -notmatch 'pwd-[A-Z0-9]+-(secret|nuova)')

try { [IO.Directory]::Delete($work, $true) } catch { } # Remove-Item non accetta il percorso breve di TEMP (I7208~1)
"=== PASS: $script:pass   FAIL: $script:fail"
