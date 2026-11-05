# Prova delle pagine console Cestino, Registro attivita' e Conflitti sul server vero: processo a parte, cartella dati usa e getta.
# Esito: PASS/FAIL per ogni controllo.  Uso: .\test-console-pages.ps1 [-Port 18671] [-Exe <Lemora.Server.exe>]
param([int]$Port = 18671, [string]$Exe = 'C:\_dev\_projects\_repos\1Remote\ui-map\_build_agent_consolepages\Lemora.Server.exe')
$work = Join-Path $env:TEMP ('pairing-test-' + [guid]::NewGuid().ToString('N')) # stesso prefisso delle altre prove: cleanup-tests.ps1 la ripulisce
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
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
function Conn($id) { @((Admin GET '/api/connections').Obj) | Where-Object { $_.id -eq $id } | Select-Object -First 1 }
function New-Pc($name, $perm) {
    $c = (Admin POST '/api/pairing/new' '{}').Obj.code
    $r = Call POST '/api/pair/request' (@{ code = $c; pc = $name; user = 'User01' } | ConvertTo-Json -Compress)
    $null = Admin POST "/api/pcs/$($r.Obj.id)/approve" (@{ permission = $perm } | ConvertTo-Json -Compress)
    $s = Call POST '/api/pair/status' (@{ id = $r.Obj.id; token = $r.Obj.token } | ConvertTo-Json -Compress) '' $false $CAPS
    [pscustomobject]@{ Id = $r.Obj.id; Key = $s.Obj.key }
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
Check '2 voci create' ($idA -and $idB)

'--- cestino'
$r = Admin GET '/api/trash'
Check 'cestino vuoto all''inizio' ($r.Code -eq 200 -and @($r.Obj).Count -eq 0) $r.Text
$null = Admin POST "/api/connections/$idB/delete" '{}'
Check 'dopo delete la voce sparisce dalle condivise' (-not (Conn $idB))
$r = Admin GET '/api/trash'
$tB = @($r.Obj) | Where-Object { $_.id -eq $idB }
Check 'la voce eliminata e'' nel cestino con nome/indirizzo/protocollo/eliminata' ($tB -and $tB.name -eq 'Voce B' -and $tB.host -eq '10.0.0.2' -and $tB.protocol -eq 'ssh' -and $tB.deletedAt) ($r.Text)
Check 'cestino: nessuna password in chiaro' (-not ($r.Text -match 'pw-b')) $r.Text
$r = Admin POST "/api/trash/$idB/restore" '{}'
Check 'ripristino -> 200' ($r.Code -eq 200) $r.Text
Check 'la voce torna tra le condivise, versione aumentata' ((Conn $idB).version -ge 2)
Check 'cestino di nuovo vuoto dopo il ripristino' (@((Admin GET '/api/trash').Obj).Count -eq 0)
$null = Admin POST "/api/connections/$idB/delete" '{}'
$r = Admin POST "/api/trash/$idB/purge" '{}'
Check 'elimina definitivamente -> 200' ($r.Code -eq 200) $r.Text
Check 'dopo purge non e'' nemmeno nel cestino' (@((Admin GET '/api/trash').Obj | Where-Object { $_.id -eq $idB }).Count -eq 0)
$r = Admin POST "/api/trash/$idB/restore" '{}'
Check 'ripristinare una voce eliminata per sempre -> 404' ($r.Code -eq 404) $r.Text
# svuota cestino
$null = Admin POST "/api/connections/$idA/delete" '{}'
$r = Admin POST '/api/trash/empty' '{}'
Check 'svuota cestino: almeno 1 eliminata, cestino vuoto' ($r.Code -eq 200 -and $r.Obj.purged -ge 1 -and @((Admin GET '/api/trash').Obj).Count -eq 0) $r.Text

'--- registro attivita'''
$r = Admin GET '/api/events?limit=200'
Check 'eventi: elenco con totale' ($r.Code -eq 200 -and $r.Obj.total -ge 1 -and @($r.Obj.items).Count -ge 1) $r.Text
Check 'eventi: c''e'' connection-created, connection-deleted, connection-restored, connection-purged, trash-emptied' (
    (@($r.Obj.items) | Where-Object { $_.kind -eq 'connection-created' }) -and
    (@($r.Obj.items) | Where-Object { $_.kind -eq 'connection-deleted' }) -and
    (@($r.Obj.items) | Where-Object { $_.kind -eq 'connection-restored' }) -and
    (@($r.Obj.items) | Where-Object { $_.kind -eq 'connection-purged' }) -and
    (@($r.Obj.items) | Where-Object { $_.kind -eq 'trash-emptied' })) $r.Text
$r = Admin GET '/api/events?kind=connection-created'
Check 'filtro per tipo: solo connection-created' (@($r.Obj.items).Count -ge 1 -and -not (@($r.Obj.items) | Where-Object { $_.kind -ne 'connection-created' })) $r.Text
Check 'registro: nessuna password in chiaro' (-not ($r.Text -match 'pw-a|pw-b')) $r.Text
$r = Admin GET '/api/events?limit=2'
Check 'paginazione: limit=2 torna 2 righe ma total maggiore' (@($r.Obj.items).Count -eq 2 -and $r.Obj.total -gt 2) $r.Text

'--- conflitti di scrittura (409 conservato)'
$pw = New-Pc 'PC-SCRITTURA' 'write'
$idC = (Admin POST '/api/connections' '{"folder":"","name":"Voce C","protocol":"rdp","host":"10.0.0.3","port":3389,"user":"u","password":"pw-c","tags":""}').Obj.id
$verC = (Conn $idC).version
# l'admin la modifica: ora e' versione +1
$null = Admin POST "/api/connections/$idC/update" '{"folder":"","name":"Voce C","protocol":"rdp","host":"10.0.0.33","port":3389,"user":"u","password":null,"tags":""}'
# il PC scrive partendo dalla versione vecchia -> 409
$upd = @{ folder = ''; name = 'Voce C dal PC'; protocol = 'rdp'; host = '10.0.0.99'; port = 3389; user = 'u'; password = 'pw-pc'; tags = 'daPC'; baseVersion = $verC }
$r = Call POST "/api/client/connections/$idC/update" ($upd | ConvertTo-Json -Compress) $pw.Key $false $CAPS
Check 'scrittura su versione vecchia -> 409 conflict (comportamento invariato)' ($r.Code -eq 409 -and $r.Obj.error -eq 'conflict') $r.Text
Check 'la voce sul server NON cambia (resta quella dell''admin)' ((Conn $idC).host -eq '10.0.0.33')
$r = Admin GET '/api/conflicts'
$cf = @($r.Obj) | Where-Object { $_.state -eq 'pending' } | Select-Object -First 1
Check 'il conflitto e'' conservato: in sospeso, PC, server vs PC' ($r.Code -eq 200 -and $cf -and $cf.pc -eq 'PC-SCRITTURA - User01' -and $cf.server.host -eq '10.0.0.33' -and $cf.client.host -eq '10.0.0.99' -and $cf.pwChanged -eq $true) ($r.Text)
Check 'conflitti: nessuna password in chiaro' (-not ($r.Text -match 'pw-pc|pw-c')) $r.Text
# ritenta: un solo conflitto in sospeso per PC+voce (il dedup elimina e reinserisce la riga con un nuovo id)
$null = Call POST "/api/client/connections/$idC/update" ($upd | ConvertTo-Json -Compress) $pw.Key $false $CAPS
Check 'un solo conflitto in sospeso anche dopo un secondo tentativo' (@((Admin GET '/api/conflicts').Obj | Where-Object { $_.state -eq 'pending' }).Count -eq 1)
# risolvi: usa la versione del PC (ri-leggo l'id, cambiato dopo il dedup)
$cf = @((Admin GET '/api/conflicts').Obj | Where-Object { $_.state -eq 'pending' })[0]
$r = Admin POST "/api/conflicts/$($cf.id)/use-pc" '{}'
Check 'usa versione del PC -> 200, la voce prende i valori del PC' ($r.Code -eq 200 -and (Conn $idC).host -eq '10.0.0.99' -and (Conn $idC).name -eq 'Voce C dal PC') $r.Text
$r = Admin POST "/api/conflicts/$($cf.id)/use-pc" '{}'
Check 'risolvere due volte -> 409 state' ($r.Code -eq 409 -and $r.Obj.error -eq 'state') $r.Text
# secondo conflitto, risolto tenendo il server
$verC2 = (Conn $idC).version
$null = Admin POST "/api/connections/$idC/update" '{"folder":"","name":"Voce C admin2","protocol":"rdp","host":"10.0.0.44","port":3389,"user":"u","password":null,"tags":""}'
$upd2 = @{ folder = ''; name = 'altro dal PC'; protocol = 'rdp'; host = '10.0.0.88'; port = 3389; user = 'u'; password = $null; tags = ''; baseVersion = $verC2 }
$null = Call POST "/api/client/connections/$idC/update" ($upd2 | ConvertTo-Json -Compress) $pw.Key $false $CAPS
$cf2 = @((Admin GET '/api/conflicts').Obj | Where-Object { $_.state -eq 'pending' })[0]
Check 'nuovo conflitto senza cambio password (pwChanged=false)' ($cf2.pwChanged -eq $false)
$r = Admin POST "/api/conflicts/$($cf2.id)/keep-server" '{}'
Check 'tieni versione server -> 200, la voce resta quella dell''admin' ($r.Code -eq 200 -and (Conn $idC).host -eq '10.0.0.44')
Check 'nessun conflitto in sospeso alla fine' (@((Admin GET '/api/conflicts').Obj | Where-Object { $_.state -eq 'pending' }).Count -eq 0)
Check 'registro: eventi conflict-recorded e conflict-used-pc/kept-server' (
    (@((Admin GET '/api/events?kind=conflict-recorded').Obj.items).Count -ge 1) -and
    (@((Admin GET '/api/events?kind=conflict-used-pc').Obj.items).Count -ge 1) -and
    (@((Admin GET '/api/events?kind=conflict-kept-server').Obj.items).Count -ge 1))

'--- senza accesso'
$r = Call GET '/api/trash'
Check 'cestino senza login -> 401' ($r.Code -eq 401) $r.Text
$r = Call GET '/api/events'
Check 'registro senza login -> 401' ($r.Code -eq 401) $r.Text
$r = Call GET '/api/conflicts'
Check 'conflitti senza login -> 401' ($r.Code -eq 401) $r.Text

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { } # Remove-Item non accetta il percorso breve di TEMP (I7208~1)
"=== PASS: $script:pass   FAIL: $script:fail"
