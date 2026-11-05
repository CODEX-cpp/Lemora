# Prova dell'abbinamento sul server vero (processo a parte, cartella dati usa e getta). Esito: PASS/FAIL per ogni controllo.
param([int]$Port = 18470, [string]$Exe = 'C:\_dev\_projects\_repos\1Remote\Server\bin\Release\net10.0\Lemora.Server.exe')
$exe = $Exe
$work = Join-Path $env:TEMP ('pairing-test-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
[IO.Directory]::CreateDirectory($data) | Out-Null  # New-Item non accetta il percorso breve di TEMP
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$base = "https://127.0.0.1:$Port"
$script:pass = 0; $script:fail = 0

function Call($method, $path, $json = $null, [bool]$csrf = $true, [string]$bearer = '', [bool]$cookies = $false) {
    $a = @('-k', '-s', '-X', $method, '-w', '\n%{http_code}')
    if ($cookies) { $a += @('-b', $jar, '-c', $jar) }
    if ($csrf) { $a += @('-H', 'X-Lemora: 1') }
    if ($bearer) { $a += @('-H', "Authorization: Bearer $bearer") }
    if ($json -ne $null) { Set-Content -Path $body -Value $json -NoNewline -Encoding ascii; $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$body") }
    $a += "$base$path"
    $out = (& curl.exe @a) -join "`n"
    $i = $out.LastIndexOf("`n")
    $text = if ($i -ge 0) { $out.Substring(0, $i) } else { '' }
    $code = [int]$out.Substring($i + 1)
    $obj = $null; try { if ($text) { $obj = $text | ConvertFrom-Json } } catch { }
    [pscustomobject]@{ Code = $code; Obj = $obj; Text = $text }
}
function Check($label, $cond, $detail = '') {
    if ($cond) { $script:pass++; "PASS  $label" } else { $script:fail++; "FAIL  $label   $detail" }
}
function Start-Server {
    $script:proc = Start-Process $exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'out.txt') -RedirectStandardError (Join-Path $work 'err.txt')
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; $r = Call GET '/api/state'; if ($r.Code -eq 200) { return } }
    throw 'server non avviato'
}

Start-Server
# configurazione iniziale (codice di avvio dal file) e accesso
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
$r = Call POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress) -cookies $true
Check 'codice di avvio' ($r.Code -eq 200)
$r = Call POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = 'Password-di-prova-123'; port = $Port; address = 'all' } | ConvertTo-Json -Compress) -cookies $true
Check 'configurazione' ($r.Code -eq 200) $r.Text
Start-Sleep 1

'--- amministratore'
$r = Call POST '/api/pairing/new' '{}'
Check 'nuovo codice SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}' -cookies $true
Check 'accesso' ($r.Code -eq 200)
$r = Call GET '/api/pairing/current' -cookies $true
Check 'nessun codice attivo' ($r.Code -eq 200 -and $r.Obj.code -eq $null) $r.Text
$r = Call POST '/api/pairing/new' '{}' -cookies $true
$code = $r.Obj.code
Check 'nuovo codice di 6 caratteri' ($r.Code -eq 200 -and $code -match '^[A-Z2-9]{6}$') $r.Text
$exp = [datetime]$r.Obj.expires
Check 'scade tra circa 30 minuti' ((($exp.ToUniversalTime() - [datetime]::UtcNow).TotalMinutes -gt 29) -and (($exp.ToUniversalTime() - [datetime]::UtcNow).TotalMinutes -le 30)) $r.Obj.expires
$r = Call GET '/api/pairing/current' -cookies $true
Check 'codice attivo visibile' ($r.Obj.code -eq $code)

'--- il PC chiede di collegarsi'
$good = @{ code = $code; pc = 'PC10'; user = 'User01'; appVersion = '1.0' }
$r = Call POST '/api/pair/request' (@{ code = 'ZZZZZZ'; pc = 'PC10'; user = 'User01' } | ConvertTo-Json -Compress)
Check 'codice sbagliato -> 401 e tentativi rimasti' ($r.Code -eq 401 -and $r.Obj.remaining -eq 4) $r.Text
$r = Call POST '/api/pair/request' (@{ code = $code; pc = "PC`t10"; user = 'User01' } | ConvertTo-Json -Compress)
Check 'nome con caratteri di controllo -> 400 (e il codice non si consuma)' ($r.Code -eq 400) "http $($r.Code)"
$r = Call POST '/api/pair/request' ($good | ConvertTo-Json -Compress) $false
Check 'richiesta SENZA intestazione CSRF -> 403' ($r.Code -eq 403) "http $($r.Code)"
$r = Call POST '/api/pair/request' ($good | ConvertTo-Json -Compress)
Check 'richiesta giusta' ($r.Code -eq 200 -and $r.Obj.id.Length -eq 32 -and $r.Obj.token.Length -eq 64 -and $r.Obj.fingerprint.Length -eq 64) $r.Text
$id = $r.Obj.id; $tok = $r.Obj.token
$r = Call POST '/api/pair/request' ($good | ConvertTo-Json -Compress)
Check 'stesso codice una seconda volta -> 401 (monouso)' ($r.Code -eq 401) "http $($r.Code)"
$r = Call POST '/api/pair/status' (@{ id = $id; token = $tok } | ConvertTo-Json -Compress)
Check 'stato: in attesa, nessuna chiave' ($r.Obj.state -eq 'pending' -and $r.Obj.key -eq $null) $r.Text
$r = Call POST '/api/pair/status' (@{ id = $id; token = ('0' * 64) } | ConvertTo-Json -Compress)
Check 'stato con token sbagliato -> 404' ($r.Code -eq 404) "http $($r.Code)"
$r = Call GET '/api/client/me'
Check 'controllo senza chiave -> 401' ($r.Code -eq 401)

'--- approvazione'
$r = Call GET '/api/pcs' -cookies $true
Check 'elenco: 1 PC in attesa' ($r.Code -eq 200 -and @($r.Obj).Count -eq 1 -and $r.Obj[0].state -eq 'pending' -and $r.Obj[0].pc -eq 'PC10' -and $r.Obj[0].user -eq 'User01') $r.Text
$r = Call GET '/api/pcs'
Check 'elenco SENZA accesso -> 401' ($r.Code -eq 401)
$r = Call POST "/api/pcs/$id/approve" '{"permission":"write","noCopy":true}'
Check 'approvazione SENZA accesso -> 401' ($r.Code -eq 401)
$r = Call POST "/api/pcs/$id/approve" '{"permission":"write","noCopy":true}' -cookies $true
Check 'approvazione' ($r.Code -eq 200) $r.Text
$r = Call POST '/api/pair/status' (@{ id = $id; token = $tok } | ConvertTo-Json -Compress)
$key = $r.Obj.key
Check 'stato: approvato con chiave, permesso scrittura, non copiare' ($r.Obj.state -eq 'approved' -and $key -match "^$id\.[0-9A-F]{64}$" -and $r.Obj.permission -eq 'write' -and $r.Obj.noCopy -eq $true) $r.Text
$r = Call POST '/api/pair/status' (@{ id = $id; token = $tok } | ConvertTo-Json -Compress)
Check 'la chiave si ripresenta finche'' il PC non conferma' ($r.Obj.key -eq $key)
$r = Call GET '/api/client/me' -bearer $key
Check 'controllo con la chiave' ($r.Code -eq 200 -and $r.Obj.state -eq 'approved' -and $r.Obj.permission -eq 'write') $r.Text
$r = Call GET '/api/client/me' -bearer ($id + '.' + ('0' * 64))
Check 'controllo con chiave sbagliata -> 401' ($r.Code -eq 401)
$r = Call POST '/api/pair/status' (@{ id = $id; token = $tok; ack = $true } | ConvertTo-Json -Compress)
Check 'conferma di ricezione: la chiave non viene piu'' mandata' ($r.Obj.state -eq 'approved' -and $r.Obj.key -eq $null) $r.Text
$r = Call GET '/api/client/me' -bearer $key
Check 'la chiave vale ancora dopo la conferma' ($r.Code -eq 200)
$r = Call GET '/api/pcs' -cookies $true
Check 'ultimo contatto registrato' ($r.Obj[0].lastSeen -ne $null) $r.Text
$r = Call POST "/api/pcs/$id/approve" '{"permission":"read"}' -cookies $true
Check 'approvare due volte -> 409' ($r.Code -eq 409)
$r = Call POST "/api/pcs/$id/update" '{"permission":"read","noCopy":false}' -cookies $true
Check 'cambio permesso' ($r.Code -eq 200)
$r = Call GET '/api/client/me' -bearer $key
Check 'il PC vede il nuovo permesso' ($r.Obj.permission -eq 'read' -and $r.Obj.noCopy -eq $false) $r.Text

'--- il server riparte: tutto resta'
Stop-Process -Id $script:proc.Id -Force; Start-Sleep 1
Start-Server
$r = Call GET '/api/client/me' -bearer $key
Check 'dopo il riavvio la chiave vale ancora' ($r.Code -eq 200 -and $r.Obj.state -eq 'approved') $r.Text
$r = Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}' -cookies $true

'--- revoca'
$r = Call POST "/api/pcs/$id/revoke" '{}' -cookies $true
Check 'revoca' ($r.Code -eq 200)
$r = Call GET '/api/client/me' -bearer $key
Check 'chiave revocata -> 401 con stato revoked' ($r.Code -eq 401 -and $r.Obj.state -eq 'revoked') $r.Text
$r = Call POST '/api/pair/status' (@{ id = $id; token = $tok } | ConvertTo-Json -Compress)
Check 'stato del PC revocato' ($r.Obj.state -eq 'revoked' -and $r.Obj.key -eq $null) $r.Text

'--- rifiuto'
$r = Call POST '/api/pairing/new' '{}' -cookies $true
$code2 = $r.Obj.code
Check 'un nuovo codice sostituisce il vecchio' ($code2 -ne $code)
$r = Call POST '/api/pair/request' (@{ code = $code2; pc = 'PC11'; user = 'User02' } | ConvertTo-Json -Compress)
$id2 = $r.Obj.id; $tok2 = $r.Obj.token
$r = Call POST "/api/pcs/$id2/reject" '{}' -cookies $true
Check 'rifiuto' ($r.Code -eq 200)
$r = Call POST '/api/pair/status' (@{ id = $id2; token = $tok2 } | ConvertTo-Json -Compress)
Check 'stato: rifiutato' ($r.Obj.state -eq 'rejected' -and $r.Obj.key -eq $null) $r.Text
$r = Call POST "/api/pcs/$id2/approve" '{"permission":"read"}' -cookies $true
Check 'approvare un rifiutato -> 409' ($r.Code -eq 409)

'--- scadenza del codice (retrodatato nel database di prova)'
$r = Call POST '/api/pairing/new' '{}' -cookies $true
$old = $r.Obj.code
Stop-Process -Id $script:proc.Id -Force; Start-Sleep 1
& python -I (Join-Path $PSScriptRoot 'backdate-code.py') (Join-Path $db 'server.db')
Start-Server
$r = Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}' -cookies $true
$r = Call GET '/api/pairing/current' -cookies $true
Check 'codice scaduto: non risulta piu'' attivo' ($r.Obj.code -eq $null) $r.Text
$r = Call POST '/api/pair/request' (@{ code = $old; pc = 'PC13'; user = 'U' } | ConvertTo-Json -Compress)
Check 'codice scaduto -> 401' ($r.Code -eq 401) "http $($r.Code)"

'--- difese'
for ($i = 1; $i -le 5; $i++) { $r = Call POST '/api/pair/request' (@{ code = 'AAAAAA'; pc = 'X'; user = 'Y' } | ConvertTo-Json -Compress) }
Check '5 codici sbagliati -> blocco' ($r.Code -eq 429) "http $($r.Code)"
$r = Call POST '/api/pairing/new' '{}' -cookies $true
$r = Call POST '/api/pair/request' (@{ code = $r.Obj.code; pc = 'PC12'; user = 'U' } | ConvertTo-Json -Compress)
Check 'anche col codice giusto, da bloccato -> 429' ($r.Code -eq 429) "http $($r.Code)"
$r = Call GET '/api/pcs' -cookies $true
Check 'nessun PC creato dai tentativi bloccati o scaduti' (@($r.Obj).Count -eq 2) $r.Text
$r = Call GET '/api/pcs' -cookies $true
Check 'elenco finale: 2 PC (revocato e rifiutato)' (@($r.Obj).Count -eq 2) $r.Text

Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
"=== PASS: $script:pass   FAIL: $script:fail"
