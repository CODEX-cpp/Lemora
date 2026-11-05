# Prova della "Durata dei collegamenti" (Sessions.cs, POST /api/client/sessions, GET /api/sessions, pagina "Collegamenti" della console) a livello API.
# Simula l'arrivo dei record di collegamento da due PC e controlla le aggregazioni admin: totali, ultimo, "in corso", "interrotta" (fine stimata dall'ultimo contatto).
# Processo del server a parte, cartella dati usa e getta, porta dedicata.  Uso: .\test-sessions.ps1 [-Port 18681] [-Exe <Lemora.Server.exe>]
param([int]$Port = 18681, [string]$Exe = '')
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$Exe = if ($Exe) { $Exe } else { Join-Path $root 'ui-map\_build_agent_durata_server\Lemora.Server.exe' }
$work = Join-Path $env:TEMP ('pairing-test-' + [guid]::NewGuid().ToString('N'))  # stesso prefisso delle altre prove: cleanup-tests.ps1 lo ripulisce
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$dbf = Join-Path $db 'server.db'; $helper = Join-Path $PSScriptRoot 'conn-db.py'
$base = "https://127.0.0.1:$Port"
$adminPw = 'Password-di-prova-123'
$CAPS = 'vnc,requests,personal,sessions'
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
function New-Pc($name, $perm) {
    $c = (Admin POST '/api/pairing/new' '{}').Obj.code
    $r = Call POST '/api/pair/request' (@{ code = $c; pc = $name; user = 'User01' } | ConvertTo-Json -Compress)
    $null = Admin POST "/api/pcs/$($r.Obj.id)/approve" (@{ permission = $perm } | ConvertTo-Json -Compress)
    $s = Call POST '/api/pair/status' (@{ id = $r.Obj.id; token = $r.Obj.token } | ConvertTo-Json -Compress) '' $false $CAPS
    [pscustomobject]@{ Id = $r.Obj.id; Key = $s.Obj.key }
}
function Rec($id, $conn, $proto, $start, $end) { $h = @{ id = $id; connId = $conn; protocol = $proto; start = $start }; if ($end) { $h.end = $end }; $h }
function SendSess($key, $items, [string]$caps = $CAPS) { Call POST '/api/client/sessions' (@{ items = @($items) } | ConvertTo-Json -Compress -Depth 4) $key $false $caps }
function Report { (Admin GET '/api/sessions').Obj }
function Iso([double]$minAgo) { $t0.AddMinutes(-$minAgo).ToString('o') }

$proc = Start-Process $Exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'out.txt') -RedirectStandardError (Join-Path $work 'err.txt')
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Call GET '/api/state').Code -eq 200) { break } }
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
$null = Admin POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress)
$r = Admin POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = $adminPw; port = $Port; address = 'all' } | ConvertTo-Json -Compress)
Check 'configurazione' ($r.Code -eq 200) $r.Text
Start-Sleep 1
$null = Admin POST '/api/login' (@{ user = 'admin'; password = $adminPw } | ConvertTo-Json -Compress)

$t0 = (Get-Date).ToUniversalTime()
$uno = New-Pc 'PC-UNO' 'write'; $due = New-Pc 'PC-DUE' 'read'
$cShared = (Admin POST '/api/connections' '{"folder":"","name":"Server Prova","protocol":"rdp","host":"10.0.0.5","port":3389,"user":"admin","password":"x","tags":""}').Obj.id
$cGhost = 'a' * 32  # voce non presente: la console mostra il fallback al posto del nome
Check '2 PC abbinati e una voce condivisa' ($uno.Key -and $due.Key -and $cShared)

'--- client vecchio e senza chiave'
$r = SendSess $uno.Key @(Rec 's0' $cShared 'rdp' (Iso 10) $null) 'vnc,requests,personal'
Check 'senza capacita'' sessions -> 400 caps, nulla salvato' ($r.Code -eq 400 -and $r.Obj.error -eq 'caps')
$r = Call POST '/api/client/sessions' '{"items":[]}' '' $false $CAPS
Check 'senza chiave -> 401' ($r.Code -eq 401) $r.Text

'--- PC-UNO: due collegamenti chiusi e uno in corso'
$r = SendSess $uno.Key @(
    (Rec 'a1' $cShared 'rdp' (Iso 120) (Iso 60)),   # chiuso, 3600 s
    (Rec 'a2' $cShared 'rdp' (Iso 30) (Iso 20)),    # chiuso, 600 s
    (Rec 'a3' $cGhost 'ssh' (Iso 10) $null),        # in corso (PC visto ora)
    (Rec 'bad' $cShared 'telnet' (Iso 5) $null),    # protocollo non valido: scartata
    (Rec 'rev' $cShared 'rdp' (Iso 5) (Iso 9)))     # fine prima dell'inizio: scartata
Check 'accettate 3 su 5 (telnet e fine<inizio scartate)' ($r.Code -eq 200 -and $r.Obj.accepted -eq 3) $r.Text

'--- PC-DUE: un collegamento ancora aperto, poi il PC "sparisce"'
$r = SendSess $due.Key @(Rec 'b1' $cShared 'vnc' (Iso 120) $null)
Check 'PC-DUE: 1 accettata' ($r.Obj.accepted -eq 1) $r.Text
# il PC-DUE non si fa piu' sentire: ultimo contatto spostato a 60 min fa (oltre i 45 di soglia)
$null = Py seen $dbf $due.Id (Iso 60)

'--- aggregazioni admin'
$rep = Report
Check 'staleMinutes = 45' ($rep.staleMinutes -eq 45) ($rep | ConvertTo-Json -Compress -Depth 5)
$pUno = @($rep.byPc) | Where-Object { $_.pcId -eq $uno.Id }
$pDue = @($rep.byPc) | Where-Object { $_.pcId -eq $due.Id }
Check 'per PC: PC-UNO 3 collegamenti, 1 in corso, nome giusto' ($pUno.count -eq 3 -and $pUno.open -eq 1 -and $pUno.pc -eq 'PC-UNO - User01') ($pUno | ConvertTo-Json -Compress)
Check 'per PC: PC-UNO tempo totale ~4800 s (3600+600+~600 in corso)' ($pUno.totalSeconds -ge 4790 -and $pUno.totalSeconds -le 4900) $pUno.totalSeconds
$lastUno = ([datetime]$pUno.lastAt).ToUniversalTime()
Check 'per PC: PC-UNO ultimo collegamento = a3 (10 min fa)' ($lastUno -ge $t0.AddMinutes(-11) -and $lastUno -le $t0.AddMinutes(-9)) $pUno.lastAt
Check 'per PC: PC-DUE interrotta -> durata stimata ~3600 s (120->60 min, fine = ultimo contatto)' ($pDue.count -eq 1 -and $pDue.open -eq 1 -and $pDue.totalSeconds -ge 3590 -and $pDue.totalSeconds -le 3610) ($pDue | ConvertTo-Json -Compress)

$kShared = @($rep.byConn) | Where-Object { $_.connId -eq $cShared }
$kGhost = @($rep.byConn) | Where-Object { $_.connId -eq $cGhost }
Check 'per voce: Server Prova 3 collegamenti da 2 PC, nome giusto' ($kShared.count -eq 3 -and $kShared.pcCount -eq 2 -and $kShared.name -eq 'Server Prova') ($kShared | ConvertTo-Json -Compress)
Check 'per voce: Server Prova tempo totale ~7800 s (3600+600+3600)' ($kShared.totalSeconds -ge 7790 -and $kShared.totalSeconds -le 7810) $kShared.totalSeconds
Check 'per voce: voce non piu'' presente -> nome nullo (la console mostra il fallback)' ($kGhost.count -eq 1 -and $null -eq $kGhost.name) ($kGhost | ConvertTo-Json -Compress)

$det = @($rep.recent)
Check 'dettaglio: 4 righe' ($det.Count -eq 4) ($det | ConvertTo-Json -Compress -Depth 4)
Check 'dettaglio: a1 concluso (closed) con fine e durata 3600' (@($det | Where-Object { $_.pc -eq 'PC-UNO - User01' -and $_.status -eq 'closed' -and $_.seconds -eq 3600 }).Count -ge 1)
Check 'dettaglio: a3 in corso (open), fine assente' (@($det | Where-Object { $_.status -eq 'open' -and $null -eq $_.end }).Count -eq 1)
Check 'dettaglio: b1 interrotto (interrupted), protocollo VNC' (@($det | Where-Object { $_.status -eq 'interrupted' -and $_.protocol -eq 'vnc' }).Count -eq 1)

'--- upsert: la fine di un collegamento aperto arriva al giro dopo'
$r = SendSess $uno.Key @(Rec 'a3' $cGhost 'ssh' (Iso 10) (Iso 2))  # stesso id, ora con fine
Check 'upsert: a3 ora chiuso, durata ~480 s' (($r.Obj.accepted -eq 1) -and ((@((Report).recent) | Where-Object { $_.status -eq 'open' }).Count -eq 0))
$a3 = @((Report).recent) | Where-Object { $_.protocol -eq 'ssh' }
Check 'upsert: a3 closed con durata 480 s (120 s * 4)' ($a3.status -eq 'closed' -and $a3.seconds -eq 480) ($a3 | ConvertTo-Json -Compress)
# la fine non torna indietro: rimandare senza fine non riapre
$null = SendSess $uno.Key @(Rec 'a3' $cGhost 'ssh' (Iso 10) $null)
$a3 = @((Report).recent) | Where-Object { $_.protocol -eq 'ssh' }
Check 'upsert: una fine gia'' scritta non si annulla' ($a3.status -eq 'closed') ($a3 | ConvertTo-Json -Compress)

$r = Call GET '/api/sessions'
Check 'aggregazioni senza accesso -> 401' ($r.Code -eq 401) $r.Text

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }  # Remove-Item non accetta il percorso breve di TEMP
"=== PASS: $script:pass   FAIL: $script:fail"
