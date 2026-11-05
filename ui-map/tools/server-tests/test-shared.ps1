# Prova di dispositivi, cartelle e credenziali di cartella condivisi (Server/Shared.cs) sul server vero: processo a parte,
# porta casuale libera, cartella dati usa e getta (la porta 25353 e i dati veri non si toccano). Esito: PASS/FAIL per ogni controllo.
param([string]$Exe = 'C:\_dev\_projects\_repos\1Remote\Server\bin\Release\net10.0\Lemora.Server.exe')
$exe = $Exe
$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $Port = $l.LocalEndpoint.Port; $l.Stop()
# stesso prefisso della prova di abbinamento: cleanup-tests.ps1 ripulisce anche questa
$work = Join-Path $env:TEMP ('pairing-test-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
[IO.Directory]::CreateDirectory($data) | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$dbf = Join-Path $db 'server.db'; $helper = Join-Path $PSScriptRoot 'conn-db.py'; $sq = Join-Path $PSScriptRoot 'shared-db.py'
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
function Sign-In { $null = Call POST '/api/login' (@{ user = 'admin'; password = $adminPw } | ConvertTo-Json -Compress) -cookies $true }
function Sql($sql, [string[]]$p = @()) { (& python -I $sq $dbf $sql @p) -join '' }
function J($o) { $o | ConvertTo-Json -Compress -Depth 6 }
function New-Pc($name, [string]$perm = 'read') {
    $c = (Call POST '/api/pairing/new' '{}' -cookies $true).Obj.code
    $r = Call POST '/api/pair/request' (J @{ code = $c; pc = $name; user = 'User01' })
    $null = Call POST "/api/pcs/$($r.Obj.id)/approve" (J @{ permission = $perm }) -cookies $true
    $key = (Call POST '/api/pair/status' (J @{ id = $r.Obj.id; token = $r.Obj.token })).Obj.key
    [pscustomobject]@{ Id = $r.Obj.id; Key = $key; Name = $name }
}
$CAPS = 'shared'
function Sync($pc, $since = 0, $caps = $CAPS) { Call GET "/api/client/sync?since=$since" -bearer $pc.Key -caps $caps }
function Dev($pc, $o) { Call POST '/api/client/devices' (J $o) -bearer $pc.Key -caps $CAPS }
function Fcr($pc, $o) { Call POST '/api/client/fcreds' (J $o) -bearer $pc.Key -caps $CAPS }
function Fo($pc, $sub, $o) { Call POST "/api/client/folders$sub" (J $o) -bearer $pc.Key -caps $CAPS }
function Conn($folder, $name) { Call POST '/api/connections' (J @{ folder = $folder; name = $name; protocol = 'rdp'; host = '10.9.9.9'; port = 3389; user = 'u'; password = ''; tags = '' }) -cookies $true }
function U($u, $p) { @{ user = $u; password = $p } }
function Names($arr) { (@($arr) | ForEach-Object { if ($_.path) { $_.path } else { $_.id } }) -join ',' }

Start-Server
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
$null = Call POST '/api/setup/verify' (J @{ code = $setup }) -cookies $true
$r = Call POST '/api/setup/complete' (J @{ dbDir = $db; user = 'admin'; password = $adminPw; port = $Port; address = 'all' }) -cookies $true
Check 'configurazione' ($r.Code -eq 200) $r.Text
Start-Sleep 1
Sign-In

$pwD1 = 'Dev-Segreta-1!x'; $pwD2 = 'Dev-Altra-2#y'; $pwF = 'Fcred-Segreta-3$z'; $pwF2 = 'Fcred-Nuova-4%w'; $pwOther = 'Altro-Disp-5&k'
$pcR = New-Pc 'PC-READ' 'read'; $pcQ = New-Pc 'PC-REQ' 'requests'; $pcW = New-Pc 'PC-WRITE' 'write'; $pcW2 = New-Pc 'PC-WRITE2' 'write'
Check '4 PC abbinati (read, requests, write, write)' ($pcR.Key -and $pcQ.Key -and $pcW.Key -and $pcW2.Key)

'--- capacita shared: i campi ci sono solo per chi la dichiara'
$r = Sync $pcW 0 ''
$n = $r.Obj.PSObject.Properties.Name
Check 'senza capacita: nessun campo devices/folders/fcreds' ($r.Code -eq 200 -and $n -notcontains 'devices' -and $n -notcontains 'folders' -and $n -notcontains 'fcreds') $r.Text
$r = Sync $pcW 0 'personal,requests'
$n = $r.Obj.PSObject.Properties.Name
Check 'con altre capacita ma non shared: ancora assenti' ($n -notcontains 'devices' -and $n -notcontains 'fcreds')
$r = Sync $pcW 0
$n = $r.Obj.PSObject.Properties.Name
Check 'con shared: tre array presenti e vuoti' ($n -contains 'devices' -and $n -contains 'folders' -and $n -contains 'fcreds' -and @($r.Obj.devices).Count -eq 0 -and @($r.Obj.folders).Count -eq 0 -and @($r.Obj.fcreds).Count -eq 0) $r.Text

'--- senza chiave / PC non scrivente'
$r = Call POST '/api/client/devices' (J @{ folder = ''; name = 'x' }) -caps $CAPS
Check 'dispositivo senza chiave -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Call POST '/api/client/folders' (J @{ path = 'x' }) -caps $CAPS
Check 'cartella senza chiave -> 401' ($r.Code -eq 401)
$r = Call GET '/api/devices'; Check 'admin: elenco dispositivi senza accesso -> 401' ($r.Code -eq 401)
$r = Call GET '/api/folders'; Check 'admin: elenco cartelle senza accesso -> 401' ($r.Code -eq 401)
$r = Call GET '/api/fcreds'; Check 'admin: elenco credenziali senza accesso -> 401' ($r.Code -eq 401)
$d0 = @{ folder = 'Sede'; name = 'Disp prova'; ip = '10.0.0.5'; users = @(U 'admin' $pwD1) }
'--- matrice permessi: creare/modificare = tutti i PC; eliminare = vietato solo a read'
$r = Dev $pcR $d0
Check 'dispositivo: PC read crea -> 200' ($r.Code -eq 200 -and $r.Obj.item.version -eq 1) $r.Text
$dr = $r.Obj.item.id
$r = Dev $pcQ @{ id = $dr; baseVersion = 1; folder = 'Sede'; name = 'Disp prova 2'; ip = '10.0.0.6'; users = @(U 'admin' $pwD1) }
Check 'dispositivo: PC requests modifica -> 200, versione 2' ($r.Code -eq 200 -and $r.Obj.item.version -eq 2) $r.Text
$r = Dev $pcR @{ id = $dr; baseVersion = 2; folder = 'Sede'; name = 'Disp prova 3'; ip = '10.0.0.7'; users = @(U 'admin' $pwD1) }
Check 'dispositivo: PC read modifica -> 200, versione 3' ($r.Code -eq 200 -and $r.Obj.item.version -eq 3) $r.Text
$r = Sync $pcW2 0
Check 'la voce creata/modificata da un PC read arriva agli altri PC' ((@($r.Obj.devices) | Where-Object { $_.id -eq $dr }).name -eq 'Disp prova 3') $r.Text
$f0 = @{ folder = 'Sede'; user = 'svc'; password = $pwF }
$r = Fcr $pcR $f0
Check 'credenziale: PC read crea -> 200' ($r.Code -eq 200 -and $r.Obj.item.version -eq 1) $r.Text
$fr = $r.Obj.item.id
$r = Fcr $pcQ @{ id = $fr; baseVersion = 1; folder = 'Sede'; user = 'svc'; password = $pwF2 }
Check 'credenziale: PC requests modifica -> 200, versione 2' ($r.Code -eq 200 -and $r.Obj.item.version -eq 2) $r.Text
$r = Fcr $pcR @{ id = $fr; baseVersion = 2; folder = 'Sede'; user = 'svc' }
Check 'credenziale: PC read modifica -> 200, versione 3' ($r.Code -eq 200 -and $r.Obj.item.version -eq 3) $r.Text
$r = Sync $pcW2 0
Check 'credenziale creata da PC read arriva agli altri' ((@($r.Obj.fcreds) | Where-Object { $_.id -eq $fr }).password -eq $pwF2)
$r = Call POST "/api/client/devices/$dr/delete" '{}' -bearer $pcR.Key -caps $CAPS; Check 'cancella dispositivo: PC read -> 403 permission' ($r.Code -eq 403 -and $r.Obj.error -eq 'permission') $r.Text
$r = Call POST "/api/client/fcreds/$fr/delete" '{}' -bearer $pcR.Key -caps $CAPS; Check 'cancella credenziale: PC read -> 403 permission' ($r.Code -eq 403 -and $r.Obj.error -eq 'permission') $r.Text
Check 'il 403 non ha cancellato nulla' ((Sql 'SELECT COUNT(*) FROM devices WHERE deleted_at IS NULL') -eq '1' -and (Sql 'SELECT COUNT(*) FROM fcreds WHERE deleted_at IS NULL') -eq '1')
$r = Call POST "/api/client/devices/$dr/delete" '{}' -bearer $pcQ.Key -caps $CAPS; Check 'cancella dispositivo: PC requests -> 200' ($r.Code -eq 200) $r.Text
$r = Call POST "/api/client/fcreds/$fr/delete" '{}' -bearer $pcW.Key -caps $CAPS; Check 'cancella credenziale: PC write -> 200' ($r.Code -eq 200) $r.Text
Check 'nulla di vivo dopo le cancellazioni' ((Sql 'SELECT COUNT(*) FROM devices WHERE deleted_at IS NULL') -eq '0' -and (Sql 'SELECT COUNT(*) FROM fcreds WHERE deleted_at IS NULL') -eq '0')

'--- dispositivi: creazione e validazione (PC write)'
$myId = 'ABCDEF0123456789abcdef0123456789'
$r = Dev $pcW @{ id = $myId; folder = 'Sede\Uffici '; name = 'Router'; ip = '10.0.0.1'; users = @((U 'admin' $pwD1), (U 'root' $pwD2)) }
$it = $r.Obj.item
Check 'crea con id proposto -> 200, id minuscolo, versione 1' ($r.Code -eq 200 -and $it.id -eq $myId.ToLower() -and $it.version -eq 1 -and $it.deleted -eq $false) $r.Text
Check 'cartella normalizzata, 2 utenti con password in chiaro' ($it.folder -eq 'Sede/Uffici' -and @($it.users).Count -eq 2 -and $it.users[0].password -eq $pwD1 -and $it.users[1].user -eq 'root')
$idD = $it.id
$r = Dev $pcW @{ folder = ''; name = 'Senza id'; ip = ''; users = @() }
Check 'crea senza id -> id generato di 32 cifre, nessun utente' ($r.Code -eq 200 -and $r.Obj.item.id -match '^[0-9a-f]{32}$' -and @($r.Obj.item.users).Count -eq 0) $r.Text
$idD2 = $r.Obj.item.id
$bad = @(
    @('nome vuoto', @{ name = '' }, 'name'), @('nome di 201 caratteri', @{ name = ('x' * 201) }, 'name'),
    @('cartella con controllo', @{ folder = "a`tb" }, 'folder'), @('cartella di 201', @{ folder = ('c' * 201) }, 'folder'),
    @('ip di 101', @{ ip = ('1' * 101) }, 'ip'),
    @('utente di 201', @{ users = @(U ('u' * 201) 'p') }, 'users'), @('password di 501', @{ users = @(U 'u' ('p' * 501)) }, 'users'),
    @('21 utenti', @{ users = @(1..21 | ForEach-Object { U "u$_" 'p' }) }, 'users'))
foreach ($t in $bad) {
    $o = @{ folder = 'F'; name = 'N'; ip = '1'; users = @() }; foreach ($k in $t[1].Keys) { $o[$k] = $t[1][$k] }
    $r = Dev $pcW $o
    Check "$($t[0]) -> 400 campo $($t[2])" ($r.Code -eq 400 -and $r.Obj.field -eq $t[2]) $r.Text
}
$r = Dev $pcW @{ folder = ('c' * 200); name = ('n' * 200); ip = ('1' * 100); users = @(1..20 | ForEach-Object { U ('u' * 200) ('p' * 500) }) }
Check 'limiti esatti (200/200/100/20 utenti/500) -> accettati' ($r.Code -eq 200) $r.Text
$null = Call POST "/api/client/devices/$($r.Obj.item.id)/delete" '{}' -bearer $pcW.Key -caps $CAPS

'--- dispositivi: modifica, conflitti, cancellazione'
$r = Dev $pcW @{ id = $idD; baseVersion = 1; folder = 'Sede/Uffici'; name = 'Router 2'; ip = '10.0.0.2'; users = @(U 'admin' $pwD1) }
Check 'modifica con baseVersion giusta -> versione 2' ($r.Code -eq 200 -and $r.Obj.item.version -eq 2 -and $r.Obj.item.name -eq 'Router 2' -and @($r.Obj.item.users).Count -eq 1) $r.Text
$r = Dev $pcW2 @{ id = $idD; baseVersion = 1; folder = 'Sede/Uffici'; name = 'Concorrente'; ip = '1'; users = @() }
Check 'baseVersion vecchia -> 409 conflict' ($r.Code -eq 409 -and $r.Obj.error -eq 'conflict') $r.Text
Check 'il conflitto non ha cambiato nulla' ((Sql 'SELECT name FROM devices WHERE id = ?' @($idD)) -eq 'Router 2')
$r = Dev $pcW @{ id = $idD; folder = 'Sede/Uffici'; name = 'Router 3'; ip = '10.0.0.3'; users = @((U 'admin' $pwD1), (U 'ospite' '')) }
Check 'id esistente senza baseVersion -> modifica (versione 3)' ($r.Code -eq 200 -and $r.Obj.item.version -eq 3) $r.Text
$r = Dev $pcW @{ id = ('1' * 32); baseVersion = 1; folder = ''; name = 'x'; users = @() }
Check 'id sconosciuto con baseVersion -> 404' ($r.Code -eq 404)
$r = Call POST "/api/client/devices/$idD2/delete" '{}' -bearer $pcW.Key -caps $CAPS
Check 'cancella -> 200' ($r.Code -eq 200)
$r = Call POST "/api/client/devices/$idD2/delete" '{}' -bearer $pcW.Key -caps $CAPS
Check 'cancella due volte -> 404' ($r.Code -eq 404)
$r = Call POST "/api/client/devices/$('2'*32)/delete" '{}' -bearer $pcW.Key -caps $CAPS
Check 'cancella inesistente -> 404' ($r.Code -eq 404)
$r = Dev $pcW @{ id = $idD2; baseVersion = 2; folder = ''; name = 'x'; users = @() }
Check 'modifica di una lapide con baseVersion -> 404' ($r.Code -eq 404)

'--- credenziali di cartella'
$r = Fcr $pcW @{ id = 'fedcba9876543210fedcba9876543210'; folder = 'Sede'; user = 'svc'; password = $pwF }
$fc = $r.Obj.item
Check 'crea credenziale -> versione 1, password in chiaro' ($r.Code -eq 200 -and $fc.version -eq 1 -and $fc.password -eq $pwF -and $fc.id -eq 'fedcba9876543210fedcba9876543210') $r.Text
$idF = $fc.id
$r = Fcr $pcW @{ folder = 'sede'; user = 'SVC'; password = 'x' }
Check 'stessa cartella+utente (maiuscole diverse) -> 409 exists' ($r.Code -eq 409 -and $r.Obj.error -eq 'exists') $r.Text
$r = Fcr $pcW @{ folder = 'Sede'; user = 'altro'; password = $pwF }
Check 'altro utente nella stessa cartella -> ok' ($r.Code -eq 200); $idF2 = $r.Obj.item.id
$r = Fcr $pcW @{ id = $idF2; folder = 'Sede'; user = 'svc' }
Check 'rinomina su un utente gia presente -> 409 exists' ($r.Code -eq 409 -and $r.Obj.error -eq 'exists')
$r = Fcr $pcW @{ id = $idF; baseVersion = 1; folder = 'Sede'; user = 'svc' }
Check 'modifica senza password -> la lascia (versione 2)' ($r.Code -eq 200 -and $r.Obj.item.version -eq 2 -and $r.Obj.item.password -eq $pwF) $r.Text
$r = Fcr $pcW2 @{ id = $idF; baseVersion = 1; folder = 'Sede'; user = 'svc'; password = 'x' }
Check 'baseVersion vecchia -> 409 conflict' ($r.Code -eq 409 -and $r.Obj.error -eq 'conflict')
$r = Fcr $pcW @{ id = $idF; baseVersion = 2; folder = 'Sede'; user = 'svc'; password = $pwF2 }
Check 'nuova password -> versione 3' ($r.Code -eq 200 -and $r.Obj.item.version -eq 3 -and $r.Obj.item.password -eq $pwF2)
$r = Fcr $pcW @{ folder = 'Sede'; user = '' }; Check 'utente vuoto -> 400 user' ($r.Code -eq 400 -and $r.Obj.field -eq 'user')
$r = Fcr $pcW @{ folder = 'Sede'; user = 'u'; password = ('p' * 501) }; Check 'password di 501 -> 400 password' ($r.Code -eq 400 -and $r.Obj.field -eq 'password')
$r = Call POST "/api/client/fcreds/$idF2/delete" '{}' -bearer $pcW.Key -caps $CAPS; Check 'cancella credenziale -> 200' ($r.Code -eq 200)
$r = Call POST "/api/client/fcreds/$idF2/delete" '{}' -bearer $pcW.Key -caps $CAPS; Check 'cancella due volte -> 404' ($r.Code -eq 404)
$r = Fcr $pcW @{ folder = 'Sede'; user = 'altro'; password = 'riuso' }
Check 'dopo la cancellazione lo stesso utente si puo ricreare' ($r.Code -eq 200)
$null = Call POST "/api/client/fcreds/$($r.Obj.item.id)/delete" '{}' -bearer $pcW.Key -caps $CAPS

'--- elenchi della console: mai password'
$a = Call GET '/api/devices' -cookies $true; $b = Call GET '/api/fcreds' -cookies $true; $c3 = Call GET '/api/folders' -cookies $true
$all = $a.Text + $b.Text + $c3.Text
Check 'elenchi admin 200' ($a.Code -eq 200 -and $b.Code -eq 200 -and $c3.Code -eq 200)
Check 'nessuna password in chiaro negli elenchi admin' (-not ($all.Contains($pwD1) -or $all.Contains($pwD2) -or $all.Contains($pwF) -or $all.Contains($pwF2)) -and $all -notmatch '"password"')
$dv = @($a.Obj) | Where-Object { $_.id -eq $idD }
Check 'dispositivo in elenco: userCount 2 e nomi utente' ($dv.userCount -eq 2 -and ($dv.users -join ',') -eq 'admin,ospite') $a.Text
Check 'credenziale in elenco: hasPassword true' ((@($b.Obj) | Where-Object { $_.id -eq $idF }).hasPassword -eq $true)
Check 'il dispositivo cancellato non e in elenco' (-not (@($a.Obj) | Where-Object { $_.id -eq $idD2 }))

'--- sincronizzazione: cursore since, lapidi, full'
$r = Sync $pcW2 0
Check 'since=0: 1 dispositivo vivo + 1 credenziale, nessuna lapide' ($r.Code -eq 200 -and @($r.Obj.devices).Count -eq 1 -and @($r.Obj.fcreds).Count -eq 1 -and $r.Text -notmatch '"deleted":true') $r.Text
$sd = $r.Obj.devices[0]; $sf = $r.Obj.fcreds[0]
Check 'sync: dispositivo con utenti e password in chiaro' ($sd.id -eq $idD -and $sd.version -eq 3 -and @($sd.users).Count -eq 2 -and $sd.users[0].password -eq $pwD1 -and $sd.ip -eq '10.0.0.3')
Check 'sync: credenziale con password in chiaro' ($sf.id -eq $idF -and $sf.password -eq $pwF2 -and $sf.version -eq 3)
$rev = $r.Obj.rev
$r = Sync $pcW2 $rev
Check 'since=rev: nulla di nuovo' (@($r.Obj.devices).Count -eq 0 -and @($r.Obj.fcreds).Count -eq 0 -and @($r.Obj.folders).Count -eq 0)
$nsync = [int](Sql "SELECT COUNT(*) FROM events WHERE kind='sync' AND pc_id=?" @($pcW2.Id))
$r = Sync $pcW2 $rev
Check 'registro: nessuna riga sync se non c e nulla' ([int](Sql "SELECT COUNT(*) FROM events WHERE kind='sync' AND pc_id=?" @($pcW2.Id)) -eq $nsync)
$r = Dev $pcW @{ id = $idD; baseVersion = 3; folder = 'Sede/Uffici'; name = 'Router 4'; ip = '10.0.0.4'; users = @(U 'admin' $pwD1) }
$r = Sync $pcW2 $rev
Check 'dopo una modifica arriva solo quel dispositivo (versione 4)' (@($r.Obj.devices).Count -eq 1 -and $r.Obj.devices[0].version -eq 4 -and @($r.Obj.fcreds).Count -eq 0) $r.Text
$rev = $r.Obj.rev
Check 'registro: la sync con voci condivise e stata registrata' ([int](Sql "SELECT COUNT(*) FROM events WHERE kind='sync' AND pc_id=?" @($pcW2.Id)) -eq $nsync + 1)
$null = Call POST "/api/client/devices/$idD/delete" '{}' -bearer $pcW.Key -caps $CAPS
$null = Call POST "/api/client/fcreds/$idF/delete" '{}' -bearer $pcW.Key -caps $CAPS
$r = Sync $pcW2 $rev
$td = $r.Obj.devices[0]; $tf = $r.Obj.fcreds[0]
Check 'lapidi: deleted=true, utenti vuoti, password vuota' ($td.id -eq $idD -and $td.deleted -eq $true -and @($td.users).Count -eq 0 -and $tf.id -eq $idF -and $tf.deleted -eq $true -and $tf.password -eq '') $r.Text
Check 'lapide: versione +1 (5 e 4)' ($td.version -eq 5 -and $tf.version -eq 4)
$rev = $r.Obj.rev
$r = Sync $pcW2 0
Check 'since=0 dopo le cancellazioni: nessuna lapide' (@($r.Obj.devices).Count -eq 0 -and @($r.Obj.fcreds).Count -eq 0 -and $r.Text -notmatch '"deleted":true')
Check 'i segreti delle lapidi sono tolti anche dal database' ((Sql 'SELECT COUNT(*) FROM devices WHERE deleted_at IS NOT NULL AND users_enc IS NOT NULL') -eq '0' -and (Sql 'SELECT COUNT(*) FROM fcreds WHERE deleted_at IS NOT NULL AND pw_enc IS NOT NULL') -eq '0')
# FullSync: il PC cambia capacita -> tutte le righe vive, non solo quelle dopo since
$dA = (Dev $pcW @{ folder = 'Full'; name = 'Per full'; ip = '1.1.1.1'; users = @(U 'u' $pwOther) }).Obj.item.id
$pcF = New-Pc 'PC-FULL' 'write'
$r = Sync $pcF 0 'personal'
$rev = $r.Obj.rev
Check 'PC senza shared: nessun campo' ($r.Obj.PSObject.Properties.Name -notcontains 'devices')
$r = Sync $pcF $rev 'personal'; Check 'stessa capacita, since=rev: niente di nuovo (sync normale)' ($r.Code -eq 200)
$r = Sync $pcF $rev 'personal,shared'
Check 'capacita cambiate (arriva shared) con since=rev: sync completa, il dispositivo vecchio arriva' ($r.Code -eq 200 -and (Names $r.Obj.devices) -match $dA) $r.Text
$r = Sync $pcF $rev 'personal,shared'
Check 'subito dopo: di nuovo normale (nessun dispositivo)' (@($r.Obj.devices).Count -eq 0)
$r = Sync $pcF $rev 'personal'
$r = Sync $pcF $rev 'shared,personal'
Check 'tornare a shared (ordine diverso, stessa chiave) dopo aver tolto la capacita: sync completa' ((Names $r.Obj.devices) -match $dA)

'--- cartelle: PC read ok senza permesso'
$r = Fo $pcR '' @{ path = 'Sede\Uffici '; domain = 'corp.lan'; ipPrefix = '10.1.' }
$fo = $r.Obj.item
Check 'PC read crea cartella -> 200, versione 1, percorso normalizzato' ($r.Code -eq 200 -and $fo.path -eq 'Sede/Uffici' -and $fo.domain -eq 'corp.lan' -and $fo.ipPrefix -eq '10.1.' -and $fo.version -eq 1 -and $fo.deleted -eq $false) $r.Text
$r = Fo $pcQ '' @{ path = 'sede/uffici'; domain = 'corp.lan'; ipPrefix = '10.1.' }
Check 'stessa cartella (maiuscole diverse), stesse impostazioni: nessun cambio, versione 1' ($r.Code -eq 200 -and $r.Obj.item.version -eq 1 -and $r.Obj.item.path -eq 'Sede/Uffici') $r.Text
$r = Fo $pcR '' @{ path = 'Sede/Uffici'; domain = 'nuovo.lan'; ipPrefix = '10.2.' }
Check 'cambio impostazioni -> versione 2' ($r.Code -eq 200 -and $r.Obj.item.version -eq 2 -and $r.Obj.item.domain -eq 'nuovo.lan')
foreach ($bp in @(@('percorso vuoto', @{ path = '' }, 'path'), @('percorso di soli separatori', @{ path = ' / ' }, 'path'), @('percorso di 201', @{ path = ('p' * 201) }, 'path'),
        @('dominio di 201', @{ path = 'x'; domain = ('d' * 201) }, 'domain'), @('prefisso di 101', @{ path = 'x'; ipPrefix = ('1' * 101) }, 'ipPrefix'))) {
    $r = Fo $pcR '' $bp[1]; Check "cartella: $($bp[0]) -> 400 campo $($bp[2])" ($r.Code -eq 400 -and $r.Obj.field -eq $bp[2]) $r.Text
}
$r = Sync $pcW2 0
$sfo = @($r.Obj.folders) | Where-Object { $_.path -eq 'Sede/Uffici' }
Check 'sync: la cartella con dominio e prefisso' ($sfo.domain -eq 'nuovo.lan' -and $sfo.ipPrefix -eq '10.2.' -and $sfo.version -eq 2)
$rev = $r.Obj.rev
$r = Fo $pcR '/delete' @{ path = 'Sede/Uffici' }
Check 'PC read cancella cartella vuota -> 200' ($r.Code -eq 200) $r.Text
$r = Sync $pcW2 $rev
$t = @($r.Obj.folders) | Where-Object { $_.path -eq 'Sede/Uffici' }
Check 'la lapide arriva al secondo PC' ($t.deleted -eq $true -and $t.version -eq 3) $r.Text
$rev = $r.Obj.rev
$r = Fo $pcR '/delete' @{ path = 'Sede/Uffici' }; Check 'cancellare di nuovo -> 404' ($r.Code -eq 404)
$r = Fo $pcR '/delete' @{ path = 'Non/Esiste' }; Check 'cartella mai esistita -> 404' ($r.Code -eq 404)
$r = Fo $pcR '' @{ path = 'Sede/Uffici'; domain = 'rinata.lan' }
Check 'ricrearla la ripristina (versione 4, vivo)' ($r.Code -eq 200 -and $r.Obj.item.version -eq 4 -and $r.Obj.item.deleted -eq $false -and $r.Obj.item.domain -eq 'rinata.lan') $r.Text

'--- cartelle: cancellazione con contenuto e a cascata'
$null = Fo $pcR '' @{ path = 'Rete' }; $null = Fo $pcR '' @{ path = 'Rete/Server' }
$cn = Conn 'Rete\Server' 'Conn condivisa'
$null = Dev $pcW @{ folder = 'Rete'; name = 'Disp in Rete'; ip = '1'; users = @(U 'u' $pwOther) }
$null = Dev $pcW @{ folder = 'Rete/Server/Rack'; name = 'Disp profondo'; ip = '2'; users = @() }
$null = Fcr $pcW @{ folder = 'Rete/Server'; user = 'cred'; password = 'pw-cred-rete' }
$r = Sync $pcW2 0; $rev = $r.Obj.rev
$r = Fo $pcR '/delete' @{ path = 'Rete' }
Check 'cartella con connessione condivisa sotto -> 409 notempty' ($r.Code -eq 409 -and $r.Obj.error -eq 'notempty') $r.Text
$r = Fo $pcW '/delete' @{ path = 'rete/server' }
Check 'anche la sottocartella (maiuscole diverse) -> 409 notempty' ($r.Code -eq 409 -and $r.Obj.error -eq 'notempty')
Check 'nulla e cambiato dopo i rifiuti' ((Sql "SELECT COUNT(*) FROM folders WHERE path LIKE 'Rete%' AND deleted_at IS NULL") -eq '2')
$null = Call POST "/api/connections/$($cn.Obj.id)/delete" '{}' -cookies $true
$r = Fo $pcR '/delete' @{ path = 'Rete' }
Check 'senza la connessione (nel cestino) -> 200' ($r.Code -eq 200) $r.Text
$r = Sync $pcW2 $rev
$delPaths = (@($r.Obj.folders) | Where-Object { $_.deleted } | ForEach-Object { $_.path } | Sort-Object) -join ','
Check 'a cascata: 2 cartelle, 2 dispositivi e 1 credenziale cancellati e sincronizzati' ($delPaths -eq 'Rete,Rete/Server' -and @($r.Obj.devices | Where-Object deleted).Count -eq 2 -and @($r.Obj.fcreds | Where-Object deleted).Count -eq 1) $r.Text
Check 'nessun dispositivo/credenziale vivo resta sotto Rete' ((Sql "SELECT COUNT(*) FROM devices WHERE folder LIKE 'Rete%' AND deleted_at IS NULL") -eq '0' -and (Sql "SELECT COUNT(*) FROM fcreds WHERE folder LIKE 'Rete%' AND deleted_at IS NULL") -eq '0')
# cartella che esiste solo perche ci vive un dispositivo (nessuna riga in folders)
$null = Dev $pcW @{ folder = 'Solo/Derivata'; name = 'D'; ip = ''; users = @() }
$r = Fo $pcR '/delete' @{ path = 'Solo' }
Check 'cartella senza riga ma con un dispositivo sotto -> 200' ($r.Code -eq 200)
Check 'il dispositivo e stato cancellato' ((Sql "SELECT COUNT(*) FROM devices WHERE folder = 'Solo/Derivata' AND deleted_at IS NULL") -eq '0')

'--- rinomina'
$null = Fo $pcR '' @{ path = 'Old'; domain = 'old.lan'; ipPrefix = '10.5.' }; $null = Fo $pcR '' @{ path = 'Old/Sub'; domain = 'sub.lan' }
$cOld = (Conn 'Old/Sub' 'Conn da spostare').Obj.id
$dOld = (Dev $pcW @{ folder = 'Old'; name = 'Disp da spostare'; ip = '3'; users = @(U 'u' $pwOther) }).Obj.item.id
$fOld = (Fcr $pcW @{ folder = 'Old/Sub'; user = 'cr'; password = 'pw-sposta' }).Obj.item.id
$dOther = (Dev $pcW @{ folder = 'Oldie'; name = 'Prefisso simile'; ip = ''; users = @() }).Obj.item.id
$pr = Call POST '/api/client/personal' (J @{ share = $true; items = @(@{ localId = 'L1'; folder = 'Old/Sub'; name = 'Personale R'; protocol = 'rdp'; host = 'h1'; port = 3389; user = 'u'; password = '' }) }) -bearer $pcR.Key -caps 'personal,shared'
Check 'connessione personale in attesa creata (PC read)' ($pr.Code -eq 200 -and $pr.Obj.items[0].state -eq 'pending') $pr.Text
$pw = Call POST '/api/client/personal' (J @{ share = $true; items = @(@{ localId = 'L2'; folder = 'Old/Sub'; name = 'Personale W'; protocol = 'rdp'; host = 'h2'; port = 3389; user = 'u'; password = '' }) }) -bearer $pcW.Key -caps 'personal,shared'
Check 'connessione personale pubblicata (PC write)' ($pw.Code -eq 200 -and $pw.Obj.items[0].state -eq 'published') $pw.Text
$before = Sync $pcW2 0; $rev = $before.Obj.rev
$vC = ($before.Obj.items | Where-Object { $_.id -eq $cOld }).version; $vD = ($before.Obj.devices | Where-Object { $_.id -eq $dOld }).version; $vF = ($before.Obj.fcreds | Where-Object { $_.id -eq $fOld }).version
$r = Fo $pcR '/rename' @{ from = 'Old'; to = 'New\Area' }
Check 'rinomina Old -> New/Area (PC read) -> 200' ($r.Code -eq 200 -and $r.Obj.moved.folders -eq 2 -and $r.Obj.moved.devices -eq 1 -and $r.Obj.moved.fcreds -eq 1 -and $r.Obj.moved.connections -ge 2 -and $r.Obj.moved.personal -ge 2) $r.Text
$r = Sync $pcW2 $rev
$fl = @($r.Obj.folders)
$nA = $fl | Where-Object { $_.path -eq 'New/Area' }; $nS = $fl | Where-Object { $_.path -eq 'New/Area/Sub' }
$oA = $fl | Where-Object { $_.path -eq 'Old' }; $oS = $fl | Where-Object { $_.path -eq 'Old/Sub' }
Check 'sync cartelle: nuovi percorsi vivi con le stesse impostazioni' ($nA.deleted -eq $false -and $nA.domain -eq 'old.lan' -and $nA.ipPrefix -eq '10.5.' -and $nS.deleted -eq $false -and $nS.domain -eq 'sub.lan')
Check 'sync cartelle: i vecchi percorsi arrivano come lapidi (versione +1)' ($oA.deleted -eq $true -and $oS.deleted -eq $true -and $oA.version -ge 2)
$ci = $r.Obj.items | Where-Object { $_.id -eq $cOld }
Check 'connessione condivisa spostata, versione +1' ($ci.folder -eq 'New/Area/Sub' -and $ci.version -eq $vC + 1) $r.Text
$di = $r.Obj.devices | Where-Object { $_.id -eq $dOld }; $fi = $r.Obj.fcreds | Where-Object { $_.id -eq $fOld }
Check 'dispositivo spostato, versione +1, password intatta' ($di.folder -eq 'New/Area' -and $di.version -eq $vD + 1 -and $di.users[0].password -eq $pwOther)
Check 'credenziale spostata, versione +1, password intatta' ($fi.folder -eq 'New/Area/Sub' -and $fi.version -eq $vF + 1 -and $fi.password -eq 'pw-sposta')
$pubItem = $r.Obj.items | Where-Object { $_.name -eq 'Personale W' }
Check 'connessione pubblicata da personale spostata' ($pubItem.folder -eq 'New/Area/Sub')
Check 'il dispositivo di una cartella dal prefisso simile (Oldie) non si muove' ((Sql 'SELECT folder FROM devices WHERE id = ?' @($dOther)) -eq 'Oldie' -and $r.Text -notmatch 'Oldie')
$pl = (Call GET '/api/personal' -cookies $true).Obj
Check 'connessioni personali (in attesa e pubblicata) spostate' ((@($pl) | Where-Object { $_.name -eq 'Personale R' }).folder -eq 'New/Area/Sub' -and (@($pl) | Where-Object { $_.name -eq 'Personale W' }).folder -eq 'New/Area/Sub')
Check 'personale: version +1' ((Sql "SELECT version FROM personal WHERE name = 'Personale R'") -eq '2')
$pw = Call POST '/api/client/personal' (J @{ share = $true; items = @(@{ localId = 'L2'; folder = 'New/Area/Sub'; name = 'Personale W'; protocol = 'rdp'; host = 'h2-bis'; port = 3389; user = 'u'; password = '' }) }) -bearer $pcW.Key -caps 'personal,shared'
Check 'dopo la rinomina il PC puo ancora aggiornare la sua connessione pubblicata (niente conflitto)' ($pw.Code -eq 200 -and $pw.Obj.items[0].state -eq 'published') $pw.Text
$r = Fo $pcR '/rename' @{ from = 'Nope'; to = 'Altro' }; Check 'rinomina di cartella inesistente -> 404' ($r.Code -eq 404)
$null = Fo $pcR '' @{ path = 'Occupata' }
$r = Fo $pcR '/rename' @{ from = 'New'; to = 'occupata' }
Check 'rinomina verso cartella esistente (maiuscole diverse) -> 409 exists' ($r.Code -eq 409 -and $r.Obj.error -eq 'exists') $r.Text
$null = Fo $pcR '' @{ path = 'Src' }; $null = Fo $pcR '' @{ path = 'Src/Dup' }; $null = Fo $pcR '' @{ path = 'Dst/Dup' }
$r = Fo $pcR '/rename' @{ from = 'Src'; to = 'Dst' }
Check 'sottocartella che finirebbe su una gia esistente -> 409 exists' ($r.Code -eq 409 -and $r.Obj.error -eq 'exists') $r.Text
$r = Fo $pcR '/rename' @{ from = 'New'; to = 'New/Area/Dentro' }; Check 'dentro se stessa -> 409 into_itself' ($r.Code -eq 409 -and $r.Obj.error -eq 'into_itself') $r.Text
$r = Fo $pcR '/rename' @{ from = 'New/Area'; to = 'new/area/x/y' }; Check 'dentro se stessa (maiuscole) -> 409 into_itself' ($r.Code -eq 409 -and $r.Obj.error -eq 'into_itself')
$r = Fo $pcR '/rename' @{ from = ''; to = 'x' }; Check 'rinomina con origine vuota -> 400 from' ($r.Code -eq 400 -and $r.Obj.field -eq 'from')
$rev = (Sync $pcW2 0).Obj.rev
$r = Fo $pcR '/rename' @{ from = 'New'; to = 'NEW' }
Check 'cambio di sole maiuscole -> 200' ($r.Code -eq 200) $r.Text
$r = Sync $pcW2 $rev
Check 'sole maiuscole: la riga resta viva con il nuovo nome e versione +1' ((@($r.Obj.folders) | Where-Object { $_.path -eq 'NEW/Area' }).deleted -eq $false -and (@($r.Obj.devices) | Where-Object { $_.id -eq $dOld }).folder -eq 'NEW/Area')
Check 'sole maiuscole: nessuna lapide creata' (-not ($r.Obj.folders | Where-Object { $_.deleted }))

'--- jolly di LIKE (_ e %) non toccano altre cartelle'
$dU = (Dev $pcW @{ folder = 'a_b'; name = 'sotto a_b'; ip = ''; users = @() }).Obj.item.id
$dX = (Dev $pcW @{ folder = 'axb'; name = 'sotto axb'; ip = ''; users = @() }).Obj.item.id
$dXc = (Dev $pcW @{ folder = 'axb/c'; name = 'sotto axb/c'; ip = ''; users = @() }).Obj.item.id
$dP = (Dev $pcW @{ folder = '100%'; name = 'sotto 100%'; ip = ''; users = @() }).Obj.item.id
$dK = (Dev $pcW @{ folder = '1000'; name = 'sotto 1000'; ip = ''; users = @() }).Obj.item.id
$null = Fo $pcR '' @{ path = 'a_b' }; $null = Fo $pcR '' @{ path = 'axb' }
$r = Fo $pcR '/delete' @{ path = '%' }; Check 'cancella "%" -> 404 (non e un jolly)' ($r.Code -eq 404)
$r = Fo $pcR '/delete' @{ path = '_' }; Check 'cancella "_" -> 404' ($r.Code -eq 404)
$r = Fo $pcR '/rename' @{ from = '%'; to = 'zzz' }; Check 'rinomina "%" -> 404' ($r.Code -eq 404)
$r = Fo $pcR '/rename' @{ from = 'a_b'; to = 'Rinominata' }
Check 'rinomina a_b -> 200' ($r.Code -eq 200 -and $r.Obj.moved.devices -eq 1) $r.Text
Check 'rinomina a_b: solo il suo dispositivo si muove (axb e axb/c restano)' ((Sql 'SELECT folder FROM devices WHERE id = ?' @($dU)) -eq 'Rinominata' -and (Sql 'SELECT folder FROM devices WHERE id = ?' @($dX)) -eq 'axb' -and (Sql 'SELECT folder FROM devices WHERE id = ?' @($dXc)) -eq 'axb/c')
$r = Fo $pcR '/rename' @{ from = '100%'; to = 'cento' }
Check 'rinomina 100%: 1000 resta dov era' ($r.Code -eq 200 -and (Sql 'SELECT folder FROM devices WHERE id = ?' @($dP)) -eq 'cento' -and (Sql 'SELECT folder FROM devices WHERE id = ?' @($dK)) -eq '1000')
$r = Fo $pcR '/delete' @{ path = 'axb' }
Check 'cancella axb: porta via axb e axb/c, non Rinominata' ($r.Code -eq 200 -and (Sql 'SELECT COUNT(*) FROM devices WHERE id IN (?,?) AND deleted_at IS NULL' @($dX, $dXc)) -eq '0' -and (Sql 'SELECT deleted_at IS NULL FROM devices WHERE id = ?' @($dU)) -eq '1')

'--- audit "credenziale copiata"'
$r = Call POST '/api/client/audit' (J @{ kind = 'copy'; what = 'password'; device = 'Router 4'; user = 'admin' }) -bearer $pcR.Key -caps $CAPS
Check 'audit da PC read -> 200' ($r.Code -eq 200) $r.Text
Check 'evento copy scritto con testo e senza password' ((Sql "SELECT detail FROM events WHERE kind='copy' AND pc_id=? ORDER BY id DESC LIMIT 1" @($pcR.Id)) -eq 'password copiata: Router 4 / admin')
$null = Call POST '/api/client/audit' (J @{ kind = 'copy'; what = 'ip'; device = 'Router'; user = '' }) -bearer $pcQ.Key -caps $CAPS
Check 'audit "ip" da PC requests -> scritto' ((Sql "SELECT COUNT(*) FROM events WHERE kind='copy' AND pc_id=?" @($pcQ.Id)) -eq '1')
$r = Call POST '/api/client/audit' (J @{ kind = 'copy'; what = 'segreto'; device = 'x'; user = 'y' }) -bearer $pcR.Key -caps $CAPS; Check 'audit con "what" sbagliato -> 400' ($r.Code -eq 400)
$r = Call POST '/api/client/audit' (J @{ kind = 'altro'; what = 'user'; device = 'x'; user = 'y' }) -bearer $pcR.Key -caps $CAPS; Check 'audit con "kind" sbagliato -> 400' ($r.Code -eq 400)
$r = Call POST '/api/client/audit' (J @{ kind = 'copy'; what = 'user'; device = 'x'; user = 'y' }) -caps $CAPS; Check 'audit senza chiave -> 401' ($r.Code -eq 401)
$evAll = Sql "SELECT group_concat(detail, '|') FROM events"
Check 'nessuna password di prova nel registro eventi' (-not ($evAll.Contains($pwD1) -or $evAll.Contains($pwD2) -or $evAll.Contains($pwF) -or $evAll.Contains($pwF2) -or $evAll.Contains($pwOther) -or $evAll.Contains('pw-cred-rete') -or $evAll.Contains('pw-sposta')))

'--- dati a riposo e riavvio'
$latin = [Text.Encoding]::GetEncoding(28591)
# il server tiene il file aperto: si legge condividendo la scrittura
function Read-Open($p) { $s = [IO.File]::Open($p, 'Open', 'Read', 'ReadWrite'); try { $m = [IO.MemoryStream]::new(); $s.CopyTo($m); $latin.GetString($m.ToArray()) } finally { $s.Dispose() } }
$raw = Read-Open $dbf
foreach ($f in @(Get-ChildItem $db -Filter 'server.db-*' -ErrorAction SilentlyContinue)) { $raw += Read-Open $f.FullName }
Check 'controllo di validita della ricerca: il nome di una cartella si trova nel file' ($raw.Contains('Rinominata'))
$leaks = @($pwD1, $pwD2, $pwF, $pwF2, $pwOther, 'pw-cred-rete', 'pw-sposta') | Where-Object { $raw.Contains($_) -or $raw.Contains([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))) }
Check 'il file del database non contiene nessuna password in chiaro (ne in base64)' (@($leaks).Count -eq 0) ($leaks -join ',')
Check 'server.key resta fuori dalla cartella del database' ((Test-Path (Join-Path $data 'server.key')) -and -not (Test-Path (Join-Path $db 'server.key')))
$keep = (Sync $pcW2 0).Text
$procId = $script:proc.Id; Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue; Start-Sleep 1
Start-Server; Sign-In
Check 'dopo il riavvio la sync da zero e identica (password si decifrano)' ((Sync $pcW2 0).Text -eq $keep)
# scambio di cifrati tra due dispositivi: AES-GCM legato all id -> errore key
$ids = @($dOld, $dU)
$a1 = Sql 'SELECT users_enc FROM devices WHERE id = ?' @($dOld); $a2 = Sql 'SELECT users_enc FROM devices WHERE id = ?' @($dOther)
Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1
$null = Sql 'SELECT 1' ; & python -I -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute('UPDATE devices SET users_enc=? WHERE id=?',(sys.argv[2],sys.argv[3])); c.commit()" $dbf $a1 $dOther
Start-Server; Sign-In
$r = Sync $pcW2 0
Check 'users cifrati copiati su un altro dispositivo -> sync 500 error key' ($r.Code -eq 500 -and $r.Obj.error -eq 'key') "http $($r.Code)"
$r = Call GET '/api/devices' -cookies $true
Check 'idem nell elenco admin -> 500 error key' ($r.Code -eq 500 -and $r.Obj.error -eq 'key')

'--- PC revocato'
$null = Call POST "/api/pcs/$($pcW.Id)/revoke" '{}' -cookies $true
$r = Dev $pcW $d0; Check 'PC revocato: scrittura -> 401 revoked' ($r.Code -eq 401 -and $r.Obj.state -eq 'revoked')
$r = Fo $pcW '' @{ path = 'x' }; Check 'PC revocato: cartella -> 401 revoked' ($r.Code -eq 401 -and $r.Obj.state -eq 'revoked')

Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
"=== PASS: $script:pass   FAIL: $script:fail"
