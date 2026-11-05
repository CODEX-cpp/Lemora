# Prova delle connessioni condivise sul server vero (processo a parte, cartella dati usa e getta). Esito: PASS/FAIL per ogni controllo.
param([int]$Port = 18473, [string]$Exe = 'C:\_dev\_projects\_repos\1Remote\Server\bin\Release\net10.0\Lemora.Server.exe')
$exe = $Exe
# stesso prefisso della prova di abbinamento: cleanup-tests.ps1 ripulisce anche questa
$work = Join-Path $env:TEMP ('pairing-test-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
[IO.Directory]::CreateDirectory($data) | Out-Null  # New-Item non accetta il percorso breve di TEMP
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$dbf = Join-Path $db 'server.db'; $helper = Join-Path $PSScriptRoot 'conn-db.py'
$base = "https://127.0.0.1:$Port"
$adminPw = 'Password-di-prova-123'
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
function Stop-TestServer { Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
# dopo ogni riavvio le sessioni non valgono piu': si rifa' l'accesso
function Sign-In { $null = Call POST '/api/login' (@{ user = 'admin'; password = $adminPw } | ConvertTo-Json -Compress) -cookies $true }
function Py { (& python -I $helper @args) -join '' }

# corpo di una voce: i valori di base si cambiano uno per uno (null = campo assente dal JSON)
function New-Body([hashtable]$over = @{}) {
    $b = @{ folder = ''; name = 'Voce base'; protocol = 'rdp'; host = '10.0.0.1'; port = 3389; user = 'utente'; password = ''; tags = '' }
    foreach ($k in $over.Keys) { if ($null -eq $over[$k]) { $b.Remove($k) } else { $b[$k] = $over[$k] } }
    $b | ConvertTo-Json -Compress
}
function Get-Conn($name) { $r = Call GET '/api/connections' -cookies $true; @($r.Obj) | Where-Object { $_.name -eq $name } | Select-Object -First 1 }
function Get-Sync($key, $since = 0) { Call GET "/api/client/sync?since=$since" -bearer $key }
# giro completo di abbinamento: nuovo codice, richiesta, (approvazione o rifiuto); restituisce id, token e chiave
function New-Pc($name, [string]$finale = 'approve') {
    $c = (Call POST '/api/pairing/new' '{}' -cookies $true).Obj.code
    $r = Call POST '/api/pair/request' (@{ code = $c; pc = $name; user = 'User01' } | ConvertTo-Json -Compress)
    $pcId = $r.Obj.id; $tok = $r.Obj.token; $key = $null
    if ($finale -ne 'pending') {
        $null = Call POST "/api/pcs/$pcId/$finale" '{"permission":"read"}' -cookies $true
        if ($finale -eq 'approve') { $key = (Call POST '/api/pair/status' (@{ id = $pcId; token = $tok } | ConvertTo-Json -Compress)).Obj.key }
    }
    [pscustomobject]@{ Id = $pcId; Token = $tok; Key = $key }
}

Start-Server
# configurazione iniziale (codice di avvio dal file) e accesso
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
$r = Call POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress) -cookies $true
Check 'codice di avvio' ($r.Code -eq 200)
$r = Call POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = $adminPw; port = $Port; address = 'all' } | ConvertTo-Json -Compress) -cookies $true
Check 'configurazione' ($r.Code -eq 200) $r.Text
Start-Sleep 1

'--- senza accesso'
$fake = '0' * 32
$r = Call GET '/api/connections'
Check 'elenco SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Call POST '/api/connections' (New-Body)
Check 'creazione SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Call POST "/api/connections/$fake/update" (New-Body)
Check 'modifica SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Call POST "/api/connections/$fake/disable" '{"disabled":true}'
Check 'disabilita SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Call POST "/api/connections/$fake/delete" '{}'
Check 'cancella SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Call POST "/api/connections/$fake/reveal" '{}'
Check 'mostra password SENZA accesso -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Call GET '/api/client/sync?since=0'
Check 'sync senza chiave -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Get-Sync ($fake + '.' + ('0' * 64))
Check 'sync con chiave sbagliata -> 401' ($r.Code -eq 401) "http $($r.Code)"
$r = Get-Sync 'chiave-senza-senso'
Check 'sync con chiave malformata -> 401' ($r.Code -eq 401) "http $($r.Code)"

Sign-In
$r = Call GET '/api/connections' -cookies $true
Check 'accesso: elenco vuoto' ($r.Code -eq 200 -and @($r.Obj).Count -eq 0) $r.Text
$pc1 = New-Pc 'PC-UNO'
Check 'PC 1 abbinato' ($pc1.Key -match '^[0-9a-f]{32}\.[0-9A-F]{64}$') "$($pc1.Key)"
$r = Get-Sync $pc1.Key
Check 'sync a archivio vuoto: nessuna voce' ($r.Code -eq 200 -and @($r.Obj.items).Count -eq 0) $r.Text

'--- validazione'
$bad = @(
    @('nome vuoto', @{ name = '' }, 'name'),
    @('nome di soli spazi', @{ name = '   ' }, 'name'),
    @('nome con carattere di controllo', @{ name = "Uno`tDue" }, 'name'),
    @('nome di 101 caratteri', @{ name = ('x' * 101) }, 'name'),
    @('protocollo telnet', @{ protocol = 'telnet' }, 'protocol'),
    @('porta 0', @{ port = 0 }, 'port'),
    @('porta 70000', @{ port = 70000 }, 'port'),
    @('host con spazi', @{ host = 'un host' }, 'host'),
    @('host vuoto', @{ host = '' }, 'host')
)
foreach ($t in $bad) {
    $r = Call POST '/api/connections' (New-Body $t[1]) -cookies $true
    Check "$($t[0]) -> 400 campo $($t[2])" ($r.Code -eq 400 -and $r.Obj.field -eq $t[2]) $r.Text
}
$r = Call POST '/api/connections' (New-Body @{ name = ('x' * 100) }) -cookies $true
Check 'nome di 100 caratteri (limite) -> accettato' ($r.Code -eq 200 -and $r.Obj.id.Length -eq 32) $r.Text
$null = Call POST "/api/connections/$($r.Obj.id)/delete" '{}' -cookies $true
$r = Call GET '/api/connections' -cookies $true
Check 'le voci rifiutate non hanno creato nulla' (@($r.Obj).Count -eq 0) $r.Text

'--- creazione'
$pwA = 'Segreta-RDP-9x!'; $pwB = 'Altra-SSH-7y#'; $pwC = 'Terza-pw-3k'
$r = Call POST '/api/connections' (New-Body @{ name = 'Server RDP'; folder = 'Uffici\Sede'; host = '10.0.0.10'; user = 'admin'; password = $pwA; tags = 'web, prod,web ' }) -cookies $true
Check 'crea RDP' ($r.Code -eq 200 -and $r.Obj.id.Length -eq 32) $r.Text
$idA = $r.Obj.id
$r = Call POST '/api/connections' (New-Body @{ name = 'Server SSH'; protocol = 'ssh'; host = 'linux.lan'; port = 22; user = 'root'; password = $pwB }) -cookies $true
Check 'crea SSH' ($r.Code -eq 200 -and $r.Obj.id.Length -eq 32) $r.Text
$idB = $r.Obj.id
$r = Call POST '/api/connections' (New-Body @{ name = 'Server C'; host = '10.0.0.30'; password = $pwC }) -cookies $true
$idC = $r.Obj.id
$r = Call GET '/api/connections' -cookies $true
Check 'elenco: 3 voci' ($r.Code -eq 200 -and @($r.Obj).Count -eq 3) $r.Text
$a = Get-Conn 'Server RDP'
Check 'cartella normalizzata Uffici/Sede' ($a.folder -eq 'Uffici/Sede') $a.folder
Check 'tag normalizzati (senza doppioni ne'' spazi)' ($a.tags -eq 'web,prod') $a.tags
Check 'RDP: dati e versione 1' ($a.protocol -eq 'rdp' -and $a.host -eq '10.0.0.10' -and $a.port -eq 3389 -and $a.user -eq 'admin' -and $a.version -eq 1 -and $a.disabled -eq $false)
$b = Get-Conn 'Server SSH'
Check 'SSH: dati' ($b.protocol -eq 'ssh' -and $b.port -eq 22 -and $b.user -eq 'root' -and $b.folder -eq '')
Check 'hasPassword true su RDP e SSH' ($a.hasPassword -eq $true -and $b.hasPassword -eq $true)
$txt = $r.Text
Check 'nessuna password in chiaro nell''elenco' (-not ($txt.Contains($pwA) -or $txt.Contains($pwB) -or $txt.Contains($pwC)) -and $txt -notmatch '"password"')

'--- cifratura'
$encA = Py enc $dbf 'Server RDP'
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pwA))
Check 'pw_enc presente e non in chiaro' ($encA.Length -gt 0 -and -not $encA.Contains($pwA)) $encA
Check 'pw_enc non contiene la password in base64' (-not $encA.Contains($b64))
$raw = [Convert]::FromBase64String($encA)
Check 'pw_enc = nonce 12 + tag 16 + testo' ($raw.Length -eq 28 + [Text.Encoding]::UTF8.GetByteCount($pwA)) "$($raw.Length)"
$keyFile = Join-Path $data 'server.key'
Check 'server.key nella cartella dei dati, 32 byte' ((Test-Path $keyFile) -and (Get-Item $keyFile).Length -eq 32)
Check 'server.key NON e'' nella cartella del database' (-not (Test-Path (Join-Path $db 'server.key')))
$keyHash = (Get-FileHash $keyFile).Hash

'--- mostra password'
$r = Call POST "/api/connections/$idA/reveal" '{}' -cookies $true
Check 'reveal RDP: password giusta' ($r.Code -eq 200 -and $r.Obj.password -eq $pwA) $r.Text
$r = Call POST "/api/connections/$idB/reveal" '{}' -cookies $true
Check 'reveal SSH: password giusta' ($r.Code -eq 200 -and $r.Obj.password -eq $pwB) $r.Text
$r = Call POST "/api/connections/$fake/reveal" '{}' -cookies $true
Check 'reveal di id inesistente -> 404' ($r.Code -eq 404) "http $($r.Code)"
Check 'registro: password-shown con il nome della voce' ([int](Py events $dbf 'password-shown' 'Server RDP') -ge 1)
Check 'registro: password-shown anche per la voce SSH' ([int](Py events $dbf 'password-shown' 'Server SSH') -ge 1)
Check 'registro: nessuna password-shown per una voce mai mostrata' ([int](Py events $dbf 'password-shown' 'Server C') -eq 0)

'--- modifica'
$r = Call POST "/api/connections/$fake/update" (New-Body) -cookies $true
Check 'modifica di id inesistente -> 404' ($r.Code -eq 404) "http $($r.Code)"
$r = Call POST "/api/connections/$idA/update" (New-Body @{ name = '' }) -cookies $true
Check 'modifica con dati sbagliati -> 400' ($r.Code -eq 400 -and $r.Obj.field -eq 'name') $r.Text
$upd = @{ name = 'Server RDP'; folder = 'Uffici\Sede'; host = '10.0.0.99'; user = 'admin'; tags = 'web, prod,web '; password = $null }
$r = Call POST "/api/connections/$idA/update" (New-Body $upd) -cookies $true
Check 'modifica senza il campo password' ($r.Code -eq 200) $r.Text
$a = Get-Conn 'Server RDP'
Check 'host cambiato, versione 2, password ancora presente' ($a.host -eq '10.0.0.99' -and $a.version -eq 2 -and $a.hasPassword -eq $true)
$r = Call POST "/api/connections/$idA/reveal" '{}' -cookies $true
Check 'password assente nella modifica -> invariata' ($r.Obj.password -eq $pwA) $r.Text
$upd.password = ''
$r = Call POST "/api/connections/$idA/update" (New-Body $upd) -cookies $true
$a = Get-Conn 'Server RDP'
Check 'password "" -> hasPassword false, versione 3' ($r.Code -eq 200 -and $a.hasPassword -eq $false -and $a.version -eq 3)
$r = Call POST "/api/connections/$idA/reveal" '{}' -cookies $true
Check 'reveal dopo averla tolta -> vuota' ($r.Code -eq 200 -and $r.Obj.password -eq '') $r.Text
Check 'pw_enc tolta anche nel database' ((Py enc $dbf 'Server RDP') -eq '')
$pwA2 = 'Nuova-pw-5z'; $upd.password = $pwA2
$r = Call POST "/api/connections/$idA/update" (New-Body $upd) -cookies $true
$a = Get-Conn 'Server RDP'
Check 'nuova password -> hasPassword true, versione 4' ($r.Code -eq 200 -and $a.hasPassword -eq $true -and $a.version -eq 4)
$r = Call POST "/api/connections/$idA/reveal" '{}' -cookies $true
Check 'reveal: la nuova password' ($r.Obj.password -eq $pwA2) $r.Text

'--- sincronizzazione (PC 1)'
$r = Get-Sync $pc1.Key 0
Check 'since=0: 3 voci, rev > 0' ($r.Code -eq 200 -and @($r.Obj.items).Count -eq 3 -and $r.Obj.rev -gt 0) $r.Text
$all1 = $r.Text; $rev = $r.Obj.rev
$sa = @($r.Obj.items) | Where-Object { $_.id -eq $idA }; $sb = @($r.Obj.items) | Where-Object { $_.id -eq $idB }; $sc = @($r.Obj.items) | Where-Object { $_.id -eq $idC }
Check 'password in chiaro nel sync' ($sa.password -eq $pwA2 -and $sb.password -eq $pwB -and $sc.password -eq $pwC)
Check 'dati completi nel sync (cartella, tag, versione)' ($sa.folder -eq 'Uffici/Sede' -and $sa.tags -eq 'web,prod' -and $sa.version -eq 4 -and $sb.protocol -eq 'ssh' -and $sa.deleted -eq $false -and $sa.disabled -eq $false)
Check 'registro: 1 riga sync per il PC 1' ([int](Py events $dbf 'sync' $pc1.Id) -eq 1)
$r = Get-Sync $pc1.Key $rev
Check 'since=rev: nessuna voce' ($r.Code -eq 200 -and @($r.Obj.items).Count -eq 0 -and $r.Obj.rev -eq $rev) $r.Text
Check 'registro: nessuna riga sync se non ci sono voci' ([int](Py events $dbf 'sync' $pc1.Id) -eq 1)

$r = Call POST "/api/connections/$idB/update" (New-Body @{ name = 'Server SSH'; protocol = 'ssh'; host = 'linux.lan'; port = 22; user = 'root'; tags = 'linux'; password = $null }) -cookies $true
$r = Get-Sync $pc1.Key $rev
Check 'dopo una modifica: solo la voce cambiata, versione 2' ($r.Code -eq 200 -and @($r.Obj.items).Count -eq 1 -and $r.Obj.items[0].id -eq $idB -and $r.Obj.items[0].version -eq 2 -and $r.Obj.items[0].tags -eq 'linux' -and $r.Obj.items[0].password -eq $pwB) $r.Text
$rev = $r.Obj.rev

$r = Call POST "/api/connections/$idC/disable" '{"disabled":true}' -cookies $true
Check 'disabilita' ($r.Code -eq 200) $r.Text
$r = Get-Sync $pc1.Key $rev
Check 'dopo disable: solo la voce con disabled=true' ($r.Code -eq 200 -and @($r.Obj.items).Count -eq 1 -and $r.Obj.items[0].id -eq $idC -and $r.Obj.items[0].disabled -eq $true -and $r.Obj.items[0].version -eq 2) $r.Text
$rev = $r.Obj.rev
Check 'l''elenco admin mostra la voce disabilitata' ((Get-Conn 'Server C').disabled -eq $true)

$r = Call POST "/api/connections/$idC/disable" '{"disabled":false}' -cookies $true
$r = Get-Sync $pc1.Key $rev
Check 'riabilitata: disabled=false' ($r.Code -eq 200 -and @($r.Obj.items).Count -eq 1 -and $r.Obj.items[0].disabled -eq $false -and $r.Obj.items[0].version -eq 3) $r.Text
$rev = $r.Obj.rev
$r = Call POST "/api/connections/$fake/disable" '{"disabled":true}' -cookies $true
Check 'disabilita id inesistente -> 404' ($r.Code -eq 404) "http $($r.Code)"

$r = Call POST "/api/connections/$idB/delete" '{}' -cookies $true
Check 'cancella' ($r.Code -eq 200) $r.Text
$r = Get-Sync $pc1.Key $rev
Check 'dopo delete: la voce con deleted=true e password null' ($r.Code -eq 200 -and @($r.Obj.items).Count -eq 1 -and $r.Obj.items[0].id -eq $idB -and $r.Obj.items[0].deleted -eq $true -and $r.Obj.items[0].password -eq $null) $r.Text
$rev = $r.Obj.rev
$r = Call GET '/api/connections' -cookies $true
Check 'l''elenco admin non la mostra piu''' ($r.Code -eq 200 -and @($r.Obj).Count -eq 2 -and -not (@($r.Obj) | Where-Object { $_.id -eq $idB }))
$r = Call POST "/api/connections/$idB/delete" '{}' -cookies $true
Check 'cancellare due volte -> 404' ($r.Code -eq 404) "http $($r.Code)"
$r = Call POST "/api/connections/$idB/reveal" '{}' -cookies $true
Check 'reveal di una voce cancellata -> 404' ($r.Code -eq 404) "http $($r.Code)"
$r = Call POST "/api/connections/$idB/update" (New-Body) -cookies $true
Check 'modifica di una voce cancellata -> 404' ($r.Code -eq 404) "http $($r.Code)"
Check 'registro: una riga sync per ogni scarico con voci (5)' ([int](Py events $dbf 'sync' $pc1.Id) -eq 5)
$r = Get-Sync $pc1.Key -5
Check 'since negativo = da zero (2 voci: quelle cancellate non si mandano a chi parte da zero)' ($r.Code -eq 200 -and @($r.Obj.items).Count -eq 2 -and -not ($r.Text -match '"deleted":true')) $r.Text

'--- secondo PC (le voci sono per tutti)'
$pc2 = New-Pc 'PC-DUE'
$r1 = Get-Sync $pc1.Key 0; $r2 = Get-Sync $pc2.Key 0
Check 'PC 2 con since=0 vede le stesse voci del PC 1' ($r2.Code -eq 200 -and @($r2.Obj.items).Count -eq 2 -and $r2.Text -eq $r1.Text) $r2.Text
Check 'registro: lo scarico del PC 2 e'' a suo nome' ([int](Py events $dbf 'sync' $pc2.Id) -eq 1)
$before1 = $r1.Text
$beforeList = (Call GET '/api/connections' -cookies $true).Text

'--- il server riparte: tutto resta'
Stop-TestServer
Start-Server
Sign-In
$r = Call GET '/api/connections' -cookies $true
Check 'dopo il riavvio l''elenco e'' uguale' ($r.Text -eq $beforeList) $r.Text
$r = Get-Sync $pc1.Key 0
Check 'dopo il riavvio il sync e'' uguale (e le password si decifrano)' ($r.Code -eq 200 -and $r.Text -eq $before1) $r.Text
$r = Call POST "/api/connections/$idA/reveal" '{}' -cookies $true
Check 'dopo il riavvio reveal funziona' ($r.Obj.password -eq $pwA2) $r.Text
Check 'server.key e'' lo stesso file' ((Get-FileHash $keyFile).Hash -eq $keyHash)

'--- password legata alla voce (scambio nel database)'
Stop-TestServer
$null = Py swap $dbf 'Server RDP' 'Server C'
Start-Server
Sign-In
$r = Call POST "/api/connections/$idA/reveal" '{}' -cookies $true
Check 'password scambiata: reveal -> 500 con error key' ($r.Code -eq 500 -and $r.Obj.error -eq 'key') $r.Text
$r = Call POST "/api/connections/$idC/reveal" '{}' -cookies $true
Check 'password scambiata (altra voce): reveal -> 500 con error key' ($r.Code -eq 500 -and $r.Obj.error -eq 'key') $r.Text
$r = Get-Sync $pc1.Key 0
Check 'password scambiata: anche il sync -> 500 con error key' ($r.Code -eq 500 -and $r.Obj.error -eq 'key') $r.Text
Stop-TestServer
$null = Py swap $dbf 'Server RDP' 'Server C'
Start-Server
Sign-In
$r = Call POST "/api/connections/$idA/reveal" '{}' -cookies $true
Check 'scambio annullato: reveal torna a funzionare' ($r.Code -eq 200 -and $r.Obj.password -eq $pwA2) $r.Text

'--- PC revocato, in attesa, rifiutato'
$r = Call POST "/api/pcs/$($pc1.Id)/revoke" '{}' -cookies $true
Check 'revoca del PC 1' ($r.Code -eq 200) $r.Text
$r = Get-Sync $pc1.Key 0
Check 'PC revocato: sync -> 401 con stato revoked' ($r.Code -eq 401 -and $r.Obj.state -eq 'revoked') $r.Text
$r = Get-Sync $pc2.Key 0
Check 'l''altro PC continua a funzionare' ($r.Code -eq 200 -and @($r.Obj.items).Count -eq 2) $r.Text
$pc3 = New-Pc 'PC-TRE' 'pending'
$r = Get-Sync ($pc3.Id + '.' + $pc3.Token)
Check 'PC in attesa: sync -> 401' ($r.Code -eq 401 -and $r.Obj.state -eq $null) $r.Text
$pc4 = New-Pc 'PC-QUATTRO' 'reject'
$r = Get-Sync ($pc4.Id + '.' + $pc4.Token)
Check 'PC rifiutato: sync -> 401' ($r.Code -eq 401 -and $r.Obj.state -eq $null) $r.Text

Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
"=== PASS: $script:pass   FAIL: $script:fail"
