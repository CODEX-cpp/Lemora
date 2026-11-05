# Prova degli amministratori della console sul server vero (processo a parte, cartella dati usa e getta). Esito: PASS/FAIL per ogni controllo.
# Regole: tutti uguali; ognuno crea altri admin; si elimina chiunque tranne se stessi e tranne il principale (quello della creazione guidata).
param([int]$Port = 18641, [string]$Exe = 'C:\_dev\_projects\_repos\1Remote\Server\bin\Release\net10.0\Lemora.Server.exe')
$exe = $Exe
# stesso prefisso delle altre prove: cleanup-tests.ps1 ripulisce anche questa
$work = Join-Path (Get-Item $env:TEMP).FullName ('pairing-test-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$body = Join-Path $work 'body.json'
$dbf = Join-Path $db 'server.db'; $helper = Join-Path $PSScriptRoot 'admins-db.py'
$base = "https://127.0.0.1:$Port"
$adminPw = 'Password-di-prova-123'; $marioPw = 'Mario-pw-segreta-77'; $lucaPw = 'Luca-pw-segreta-88'; $annaPw = 'Anna-pw-segreta-99'
$script:pass = 0; $script:fail = 0

# $jar: nome della "persona" (file dei cookie separato per ognuna), '' = senza cookie
function Call($method, $path, $json = $null, [string]$jar = '', [bool]$csrf = $true) {
    $a = @('-k', '-s', '-X', $method, '-w', '\n%{http_code}')
    if ($jar) { $f = Join-Path $work "$jar.txt"; $a += @('-b', $f, '-c', $f) }
    if ($csrf) { $a += @('-H', 'X-Lemora: 1') }
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
function Stop-TestServer { Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
function Login($user, $pw, $jar) { Call POST '/api/login' (@{ user = $user; password = $pw } | ConvertTo-Json -Compress) $jar }
function NewAdmin($user, $pw, $jar) { Call POST '/api/admins' (@{ user = $user; password = $pw } | ConvertTo-Json -Compress) $jar }
function DelAdmin($user, $jar) { Call POST "/api/admins/$([uri]::EscapeDataString($user))/delete" '{}' $jar }
function Py { (& python -I $helper @args) -join '' }
function Row($list, $user) { @($list) | Where-Object { $_.user -eq $user } | Select-Object -First 1 }

try {
Start-Server
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
$r = Call POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress) 'setup'
Check 'codice di avvio' ($r.Code -eq 200)
$r = Call POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = $adminPw; port = $Port; address = 'all' } | ConvertTo-Json -Compress) 'setup'
Check 'configurazione (crea il primo admin)' ($r.Code -eq 200) $r.Text
Start-Sleep 1

'--- senza accesso'
$r = Call GET '/api/admins'
Check 'elenco SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = NewAdmin 'x' $adminPw ''
Check 'crea SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = DelAdmin 'admin' ''
Check 'elimina SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"

'--- primo admin'
$r = Login 'admin' $adminPw 'A'
Check 'login admin' ($r.Code -eq 200) $r.Text
$r = Call GET '/api/me' $null 'A'
Check '/me dice chi e'' collegato' ($r.Obj.user -eq 'admin') $r.Text
$r = Call GET '/api/admins' $null 'A'
$a = Row $r.Obj 'admin'
Check 'elenco: un admin, principale, (tu), creato dalla creazione guidata' (@($r.Obj).Count -eq 1 -and $a.main -eq $true -and $a.me -eq $true -and $a.createdBy -eq $null -and $a.createdAt) $r.Text
Check 'elenco senza hash' ($r.Text -notmatch 'pbkdf2|hash') $r.Text
$r = Call POST '/api/admins' (@{ user = 'x1'; password = $adminPw } | ConvertTo-Json -Compress) 'A' $false
Check 'POST senza intestazione X-Lemora -> 403' ($r.Code -eq 403) "http $($r.Code)"

'--- validazione'
foreach ($t in @(@('utente vuoto', '', $marioPw, 400, 'user'), @('utente con spazio', 'mario rossi', $marioPw, 400, 'user'), @('utente con <', 'ma<rio', $marioPw, 400, 'user'),
                 @('utente di 65 caratteri', ('m' * 65), $marioPw, 400, 'user'), @('password di 9 caratteri', 'mario', '123456789', 400, 'password'),
                 @('doppione ADMIN (maiuscole)', 'ADMIN', $marioPw, 409, 'exists'))) {
    $r = NewAdmin $t[1] $t[2] 'A'
    Check "$($t[0]) -> $($t[3]) $($t[4])" ($r.Code -eq $t[3] -and $r.Obj.error -eq $t[4]) $r.Text
}

'--- creare, accedere, creare ancora'
$r = NewAdmin 'mario' $marioPw 'A'
Check 'admin crea mario' ($r.Code -eq 200) $r.Text
$r = Login 'Mario' $marioPw 'B'
Check 'login con il nuovo (maiuscole diverse ok)' ($r.Code -eq 200) $r.Text
$r = Call GET '/api/me' $null 'B'
Check '/me di mario = mario (nome salvato)' ($r.Obj.user -eq 'mario') $r.Text
$r = NewAdmin 'luca' $lucaPw 'B'
Check 'mario crea luca' ($r.Code -eq 200) $r.Text
$r = NewAdmin 'anna.b@uff' $annaPw 'B'
Check 'mario crea anna.b@uff' ($r.Code -eq 200) $r.Text
$r = Call GET '/api/admins' $null 'B'
Check 'elenco: 4 admin, creato da giusto, (tu) solo su mario' (@($r.Obj).Count -eq 4 -and (Row $r.Obj 'luca').createdBy -eq 'mario' -and (Row $r.Obj 'mario').createdBy -eq 'admin' -and (Row $r.Obj 'mario').me -eq $true -and (Row $r.Obj 'admin').me -eq $false -and @($r.Obj | Where-Object main).Count -eq 1) $r.Text
Check 'il principale e'' il primo dell''elenco' (@($r.Obj)[0].user -eq 'admin')

'--- eliminare'
$null = Login 'luca' $lucaPw 'L'
$r = DelAdmin 'luca' 'B'
Check 'mario elimina luca' ($r.Code -eq 200) $r.Text
$r = Call GET '/api/admins' $null 'L'
Check 'sessione di luca chiusa subito -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Login 'luca' $lucaPw 'L'
Check 'luca non entra piu'' -> 401' ($r.Code -eq 401) $r.Text
$r = DelAdmin 'mario' 'B'
Check 'mario non elimina se stesso -> 403 self' ($r.Code -eq 403 -and $r.Obj.error -eq 'self' -and $r.Obj.message) $r.Text
$r = DelAdmin 'admin' 'B'
Check 'mario non elimina il principale -> 403 main' ($r.Code -eq 403 -and $r.Obj.error -eq 'main' -and $r.Obj.message -match 'principale') $r.Text
$r = DelAdmin 'admin' 'A'
Check 'il principale non elimina se stesso -> 403' ($r.Code -eq 403) $r.Text
$r = DelAdmin 'nessuno' 'A'
Check 'eliminare chi non esiste -> 404' ($r.Code -eq 404) $r.Text
$r = Login 'anna.b@uff' $annaPw 'C'
Check 'login anna' ($r.Code -eq 200) $r.Text
$r = DelAdmin 'MARIO' 'C'
Check 'anna elimina mario (tra admin non principali)' ($r.Code -eq 200) $r.Text
$r = Call GET '/api/admins' $null 'B'
Check 'sessione di mario chiusa subito -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = DelAdmin 'anna.b@uff' 'A'
Check 'il principale elimina anna' ($r.Code -eq 200) $r.Text
$r = Call GET '/api/admins' $null 'C'
Check 'sessione di anna chiusa subito -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Call GET '/api/admins' $null 'A'
Check 'resta solo il principale, ancora collegato' ($r.Code -eq 200 -and @($r.Obj).Count -eq 1 -and $r.Obj[0].user -eq 'admin') $r.Text

'--- registro e database'
$created = Py events $dbf 'admin-created'; $deleted = Py events $dbf 'admin-deleted'
Check 'registro: 4 admin-created (con chi ha agito)' ($created -eq 'admin (creazione guidata)|mario da admin|luca da mario|anna.b@uff da mario') $created
Check 'registro: 3 admin-deleted (con chi ha agito)' ($deleted -eq 'luca da mario|mario da anna.b@uff|anna.b@uff da admin') $deleted
$rows = Py rows $dbf | ConvertFrom-Json
Check 'hash PBKDF2 nel database' ($rows[0].hash -match '^pbkdf2-sha256\$600000\$') $rows[0].hash
$leak = foreach ($p in @($adminPw, $marioPw, $lucaPw, $annaPw)) { Py contains $dbf $p }
Check 'nessuna password in chiaro nel file del database' (-not ($leak -contains '1')) ($leak -join ',')
$logs = (Get-Content (Join-Path $work 'out.txt'), (Join-Path $work 'err.txt') -Raw -ErrorAction SilentlyContinue) -join ''
Check 'nessuna password nei log del server' (-not ($logs -and ($logs.Contains($adminPw) -or $logs.Contains($marioPw))))

'--- migrazione dal formato vecchio (un solo admin nella tabella admin)'
Stop-TestServer
$oldHash = Py to-old $dbf
Check 'database riportato al formato vecchio' ((Py tables $dbf) -match '(^|,)admin(,|$)' -and (Py tables $dbf) -notmatch 'admins') (Py tables $dbf)
Start-Server
$r = Login 'admin' $adminPw 'A'
Check 'dopo la migrazione: login con la vecchia password' ($r.Code -eq 200) $r.Text
$r = Call GET '/api/admins' $null 'A'
Check 'dopo la migrazione: admin primo della lista, principale' (@($r.Obj).Count -eq 1 -and $r.Obj[0].user -eq 'admin' -and $r.Obj[0].main -eq $true -and $r.Obj[0].createdAt) $r.Text
$rows = Py rows $dbf | ConvertFrom-Json
Check 'hash conservato identico, tabella vecchia tolta' ($rows[0].hash -eq $oldHash -and (Py tables $dbf) -notmatch '(^|,)admin(,|$)') (Py tables $dbf)
$r = Call GET '/api/state'
Check 'nessuna creazione guidata da rifare' ($r.Obj.configured -eq $true)
$r = NewAdmin 'dopo' $marioPw 'A'
$r2 = DelAdmin 'admin' 'A'
Check 'dopo la migrazione si creano admin e il principale resta protetto' ($r.Code -eq 200 -and $r2.Code -eq 403) "$($r.Text) $($r2.Text)"

'--- blocco per troppi tentativi (resta)'
$codes = foreach ($i in 1..5) { (Login 'admin' 'sbagliata-123456' '').Code }
$r = Login 'admin' $adminPw ''
Check '5 errori -> bloccato, anche la password giusta -> 429' ($codes[-1] -eq 429 -and $r.Code -eq 429) "$($codes -join ',') / $($r.Code)"
}
finally {
    Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
"=== PASS: $script:pass   FAIL: $script:fail"
