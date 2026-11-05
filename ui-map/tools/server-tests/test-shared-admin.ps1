# Prova delle azioni dell'amministratore sulla pagina "Credenziali e cartelle" (Server/Shared.cs, POST /api/folders*, /api/devices/{id}/delete, /api/fcreds/{id}/delete)
# sul server vero: processo a parte, porta casuale libera, cartella dati usa e getta (la porta 25353 e i dati veri non si toccano). Esito: PASS/FAIL per ogni controllo.
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
function New-Pc($name, [string]$perm = 'read') {
    $c = (Call POST '/api/pairing/new' '{}' -cookies $true).Obj.code
    $r = Call POST '/api/pair/request' (J @{ code = $c; pc = $name; user = 'User01' })
    $null = Call POST "/api/pcs/$($r.Obj.id)/approve" (J @{ permission = $perm }) -cookies $true
    $key = (Call POST '/api/pair/status' (J @{ id = $r.Obj.id; token = $r.Obj.token })).Obj.key
    [pscustomobject]@{ Id = $r.Obj.id; Key = $key; Name = $name }
}
$CAPS = 'shared'
function Sync($pc, $since = 0) { Call GET "/api/client/sync?since=$since" -bearer $pc.Key -caps $CAPS }
function Dev($pc, $o) { Call POST '/api/client/devices' (J $o) -bearer $pc.Key -caps $CAPS }
function Fcr($pc, $o) { Call POST '/api/client/fcreds' (J $o) -bearer $pc.Key -caps $CAPS }
function Adm($path, $o = @{}) { Call POST $path (J $o) -cookies $true }
function Conn($folder, $name) { Call POST '/api/connections' (J @{ folder = $folder; name = $name; protocol = 'rdp'; host = '10.9.9.9'; port = 3389; user = 'u'; password = ''; tags = '' }) -cookies $true }
function U($u, $p) { @{ user = $u; password = $p } }
function AdminEv($kind) { [int](Sql "SELECT COUNT(*) FROM events WHERE kind=? AND pc_id IS NULL" @($kind)) }
function Tomb($arr, $prop, $val) { @($arr) | Where-Object { $_.$prop -eq $val -and $_.deleted -eq $true } | Select-Object -First 1 }
function FolderRow($path) { @((Call GET '/api/folders' -cookies $true).Obj) | Where-Object { $_.path -eq $path } }

Start-Server
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
$null = Call POST '/api/setup/verify' (J @{ code = $setup }) -cookies $true
$r = Call POST '/api/setup/complete' (J @{ dbDir = $db; user = 'admin'; password = $adminPw; port = $Port; address = 'all' }) -cookies $true
Check 'configurazione' ($r.Code -eq 200) $r.Text
Start-Sleep 1

'--- senza sessione / senza protezione CSRF'
$x = '0' * 32
foreach ($p in @(@('/api/folders', @{ path = 'x' }), @('/api/folders/delete', @{ path = 'x' }), @('/api/folders/rename', @{ from = 'x'; to = 'y' }), @("/api/devices/$x/delete", @{}), @("/api/fcreds/$x/delete", @{}))) {
    $r = Call POST $p[0] (J $p[1]); Check "$($p[0]) senza accesso -> 401" ($r.Code -eq 401) "http $($r.Code)"
}
$null = Call POST '/api/login' (J @{ user = 'admin'; password = $adminPw }) -cookies $true
foreach ($p in @(@('/api/folders', @{ path = 'x' }), @('/api/folders/delete', @{ path = 'x' }), @('/api/folders/rename', @{ from = 'x'; to = 'y' }), @("/api/devices/$x/delete", @{}), @("/api/fcreds/$x/delete", @{}))) {
    $r = Call POST $p[0] (J $p[1]) -csrf $false -cookies $true; Check "$($p[0]) con sessione ma senza X-Lemora -> 403" ($r.Code -eq 403) "http $($r.Code)"
}
$r = Call POST '/api/folders' (J @{ path = 'x' }) -bearer 'abc.def' ; Check 'chiave di un PC (non sessione) non basta -> 401' ($r.Code -eq 401)

$pcW = New-Pc 'PC-WRITE' 'write'; $pcW2 = New-Pc 'PC-WRITE2' 'write'; $pcR = New-Pc 'PC-READ' 'read'
Check 'nulla creato dai rifiuti' ((Sql 'SELECT COUNT(*) FROM folders') -eq '0')
$pwA = 'Adm-Disp-1!a'; $pwB = 'Adm-Cred-2#b'

'--- cartelle: crea, modifica, validazione'
$rev = (Sync $pcW2 0).Obj.rev
$r = Adm '/api/folders' @{ path = 'Admin\Area '; domain = 'x.lan'; ipPrefix = '10.7.' }
$fo = $r.Obj.item
Check 'crea -> 200, percorso normalizzato, versione 1' ($r.Code -eq 200 -and $fo.path -eq 'Admin/Area' -and $fo.domain -eq 'x.lan' -and $fo.ipPrefix -eq '10.7.' -and $fo.version -eq 1 -and $fo.deleted -eq $false) $r.Text
$s = Sync $pcW2 $rev
Check 'il PC riceve la nuova cartella' ((@($s.Obj.folders) | Where-Object { $_.path -eq 'Admin/Area' }).domain -eq 'x.lan')
Check 'registro: folder-created senza PC (amministratore)' ((AdminEv 'folder-created') -eq 1 -and (Sql "SELECT detail FROM events WHERE kind='folder-created'") -eq 'Admin/Area')
$r = Adm '/api/folders' @{ path = 'admin/area'; domain = 'x.lan'; ipPrefix = '10.7.' }
Check 'stesse impostazioni (maiuscole diverse): nessun cambio, versione 1, nessun nuovo evento' ($r.Obj.item.version -eq 1 -and (AdminEv 'folder-updated') -eq 0)
$rev = (Sync $pcW2 0).Obj.rev
$r = Adm '/api/folders' @{ path = 'Admin/Area'; domain = 'y.lan'; ipPrefix = '10.8.' }
Check 'cambio dominio e prefisso -> versione 2' ($r.Code -eq 200 -and $r.Obj.item.version -eq 2 -and $r.Obj.item.domain -eq 'y.lan' -and $r.Obj.item.ipPrefix -eq '10.8.') $r.Text
$s = Sync $pcW2 $rev
Check 'il PC riceve la modifica (versione 2)' ((@($s.Obj.folders) | Where-Object { $_.path -eq 'Admin/Area' }).version -eq 2)
Check 'registro: folder-updated senza PC' ((AdminEv 'folder-updated') -eq 1)
$r = Adm '/api/folders' @{ path = '' }; Check 'percorso vuoto -> 400 path' ($r.Code -eq 400 -and $r.Obj.field -eq 'path')
$r = Adm '/api/folders' @{ path = 'ok'; domain = ('d' * 201) }; Check 'dominio di 201 -> 400 domain' ($r.Code -eq 400 -and $r.Obj.field -eq 'domain')
$r = Adm '/api/folders' @{ path = 'ok'; ipPrefix = ('1' * 101) }; Check 'prefisso di 101 -> 400 ipPrefix' ($r.Code -eq 400 -and $r.Obj.field -eq 'ipPrefix')

'--- elenchi: conteggi delle voci dentro, mai password'
$null = Adm '/api/folders' @{ path = 'Admin/Area/Sub' }
$cn = Conn 'Admin/Area' 'Conn in cartella'
$dA = (Dev $pcW @{ folder = 'Admin/Area/Sub'; name = 'Disp A'; ip = '10.8.0.1'; users = @((U 'root' $pwA), (U 'ospite' '')) }).Obj.item.id
$dB = (Dev $pcW @{ folder = 'Admin/Area'; name = 'Disp B'; ip = '10.8.0.2'; users = @(U 'u' $pwA) }).Obj.item.id
$fA = (Fcr $pcW @{ folder = 'Admin/Area'; user = 'svc'; password = $pwB }).Obj.item.id
$fB = (Fcr $pcW @{ folder = 'Admin/Area/Sub'; user = 'svc2'; password = $pwB }).Obj.item.id
$gf = Call GET '/api/folders' -cookies $true; $gd = Call GET '/api/devices' -cookies $true; $gc = Call GET '/api/fcreds' -cookies $true
$ar = FolderRow 'Admin/Area'; $sr = FolderRow 'Admin/Area/Sub'
Check 'Admin/Area: 1 connessione, 2 dispositivi, 2 credenziali (sottocartella compresa), entries 5' ($ar.connections -eq 1 -and $ar.devices -eq 2 -and $ar.fcreds -eq 2 -and $ar.entries -eq 5) (J $ar)
Check 'Admin/Area/Sub: 0 connessioni, 1 dispositivo, 1 credenziale' ($sr.connections -eq 0 -and $sr.devices -eq 1 -and $sr.fcreds -eq 1 -and $sr.entries -eq 2) (J $sr)
$all = $gf.Text + $gd.Text + $gc.Text
Check 'liste admin: nessuna password in chiaro e nessun campo password' (-not ($all.Contains($pwA) -or $all.Contains($pwB)) -and $all -notmatch '"password"')
$dv = @($gd.Obj) | Where-Object { $_.id -eq $dA }
Check 'dispositivo in elenco: cartella, nome, IP, nomi utente, numero utenti' ($dv.folder -eq 'Admin/Area/Sub' -and $dv.name -eq 'Disp A' -and $dv.ip -eq '10.8.0.1' -and $dv.userCount -eq 2 -and ($dv.users -join ',') -eq 'root,ospite') (J $dv)
Check 'credenziale in elenco: cartella, utente, hasPassword' ((@($gc.Obj) | Where-Object { $_.id -eq $fA }).hasPassword -eq $true -and (@($gc.Obj) | Where-Object { $_.id -eq $fA }).user -eq 'svc')

'--- eliminare una cartella: rifiutata se contiene connessioni condivise'
$rev = (Sync $pcW2 0).Obj.rev
$r = Adm '/api/folders/delete' @{ path = 'Admin/Area' }
Check 'con una connessione dentro -> 409 notempty' ($r.Code -eq 409 -and $r.Obj.error -eq 'notempty') $r.Text
$r = Adm '/api/folders/delete' @{ path = 'admin/area/sub' }
Check 'sottocartella senza connessioni (la connessione e'' nella cartella sopra) -> 200' ($r.Code -eq 200) $r.Text
$s = Sync $pcW2 $rev
Check 'sub cancellata a cascata: lapidi di cartella, dispositivo A e credenziale B' ((Tomb $s.Obj.folders 'path' 'Admin/Area/Sub') -and (Tomb $s.Obj.devices 'id' $dA) -and (Tomb $s.Obj.fcreds 'id' $fB)) $s.Text
Check 'registro: folder-deleted senza PC' ((AdminEv 'folder-deleted') -eq 1)
Check 'Admin/Area intatta (disp B e credenziale A vivi)' ((Sql 'SELECT COUNT(*) FROM devices WHERE id=? AND deleted_at IS NULL' @($dB)) -eq '1' -and (Sql 'SELECT COUNT(*) FROM fcreds WHERE id=? AND deleted_at IS NULL' @($fA)) -eq '1')
$r = Adm '/api/folders/delete' @{ path = 'Non/Esiste' }; Check 'cartella inesistente -> 404' ($r.Code -eq 404)
$r = Adm '/api/folders/delete' @{ path = '' }; Check 'percorso vuoto -> 400' ($r.Code -eq 400 -and $r.Obj.field -eq 'path')

'--- rinomina'
$null = Adm '/api/folders' @{ path = 'Occupata' }
$r = Adm '/api/folders/rename' @{ from = 'Admin/Area'; to = 'occupata' }; Check 'verso cartella esistente (maiuscole diverse) -> 409 exists' ($r.Code -eq 409 -and $r.Obj.error -eq 'exists') $r.Text
$r = Adm '/api/folders/rename' @{ from = 'Admin/Area'; to = 'Admin/Area/Dentro' }; Check 'dentro se stessa -> 409 into_itself' ($r.Code -eq 409 -and $r.Obj.error -eq 'into_itself')
$r = Adm '/api/folders/rename' @{ from = 'Nope'; to = 'Altro' }; Check 'origine inesistente -> 404' ($r.Code -eq 404)
$r = Adm '/api/folders/rename' @{ from = ''; to = 'x' }; Check 'origine vuota -> 400 from' ($r.Code -eq 400 -and $r.Obj.field -eq 'from')
$before = Sync $pcW2 0; $rev = $before.Obj.rev
$vD = ($before.Obj.devices | Where-Object { $_.id -eq $dB }).version; $vF = ($before.Obj.fcreds | Where-Object { $_.id -eq $fA }).version; $vC = ($before.Obj.items | Where-Object { $_.id -eq $cn.Obj.id }).version
$r = Adm '/api/folders/rename' @{ from = 'Admin\Area'; to = 'Admin/Zona' }
Check 'rinomina Admin/Area -> Admin/Zona -> 200, 1 cartella, 1 connessione, 1 dispositivo, 1 credenziale' ($r.Code -eq 200 -and $r.Obj.moved.folders -eq 1 -and $r.Obj.moved.connections -eq 1 -and $r.Obj.moved.devices -eq 1 -and $r.Obj.moved.fcreds -eq 1) $r.Text
$s = Sync $pcW2 $rev
$nz = @($s.Obj.folders) | Where-Object { $_.path -eq 'Admin/Zona' }; $oz = @($s.Obj.folders) | Where-Object { $_.path -eq 'Admin/Area' }
Check 'il PC riceve: nuovo percorso vivo con le stesse impostazioni, vecchio come lapide' ($nz.deleted -eq $false -and $nz.domain -eq 'y.lan' -and $nz.ipPrefix -eq '10.8.' -and $oz.deleted -eq $true)
Check 'dispositivo, credenziale e connessione spostati con versione +1, password intatta' ((($s.Obj.devices | Where-Object { $_.id -eq $dB }).folder -eq 'Admin/Zona') -and (($s.Obj.devices | Where-Object { $_.id -eq $dB }).version -eq $vD + 1) -and (($s.Obj.devices | Where-Object { $_.id -eq $dB }).users[0].password -eq $pwA) -and (($s.Obj.fcreds | Where-Object { $_.id -eq $fA }).folder -eq 'Admin/Zona') -and (($s.Obj.fcreds | Where-Object { $_.id -eq $fA }).version -eq $vF + 1) -and (($s.Obj.fcreds | Where-Object { $_.id -eq $fA }).password -eq $pwB) -and (($s.Obj.items | Where-Object { $_.id -eq $cn.Obj.id }).folder -eq 'Admin/Zona') -and (($s.Obj.items | Where-Object { $_.id -eq $cn.Obj.id }).version -eq $vC + 1))
Check 'registro: folder-renamed senza PC con "da -> a"' ((AdminEv 'folder-renamed') -eq 1 -and (Sql "SELECT detail FROM events WHERE kind='folder-renamed'") -eq 'Admin/Area -> Admin/Zona')

'--- eliminare una cartella dopo aver tolto la connessione'
$r = Adm '/api/folders/delete' @{ path = 'Admin/Zona' }; Check 'ancora la connessione dentro -> 409 notempty' ($r.Code -eq 409)
$null = Call POST "/api/connections/$($cn.Obj.id)/delete" '{}' -cookies $true
$rev = (Sync $pcW2 0).Obj.rev
$r = Adm '/api/folders/delete' @{ path = 'Admin/Zona' }; Check 'con la connessione nel cestino -> 200' ($r.Code -eq 200) $r.Text
$s = Sync $pcW2 $rev
$tdB = Tomb $s.Obj.devices 'id' $dB
Check 'lapidi sincronizzate: cartella, disp B (versione +2 dall''inizio), credenziale A' ((Tomb $s.Obj.folders 'path' 'Admin/Zona') -and $tdB -and $tdB.version -eq ($vD + 2) -and (Tomb $s.Obj.fcreds 'id' $fA)) $s.Text
Check 'i segreti sono tolti dal database' ((Sql 'SELECT COUNT(*) FROM devices WHERE deleted_at IS NOT NULL AND users_enc IS NOT NULL') -eq '0' -and (Sql 'SELECT COUNT(*) FROM fcreds WHERE deleted_at IS NOT NULL AND pw_enc IS NOT NULL') -eq '0')

'--- eliminare dispositivi e credenziali'
$d1 = (Dev $pcW @{ folder = 'Solo'; name = 'Da togliere'; ip = '1.2.3.4'; users = @(U 'u' $pwA) }).Obj.item.id
$f1 = (Fcr $pcW @{ folder = 'Solo'; user = 'dismesso'; password = $pwB }).Obj.item.id
$s0 = Sync $pcW2 0; $rev = $s0.Obj.rev; $v1 = ($s0.Obj.devices | Where-Object { $_.id -eq $d1 }).version; $v2 = ($s0.Obj.fcreds | Where-Object { $_.id -eq $f1 }).version
$r = Adm "/api/devices/$d1/delete"; Check 'elimina dispositivo -> 200' ($r.Code -eq 200) $r.Text
$r = Adm "/api/devices/$d1/delete"; Check 'di nuovo -> 404' ($r.Code -eq 404)
$r = Adm "/api/fcreds/$f1/delete"; Check 'elimina credenziale -> 200' ($r.Code -eq 200) $r.Text
$r = Adm "/api/fcreds/$f1/delete"; Check 'di nuovo -> 404' ($r.Code -eq 404)
$r = Adm "/api/devices/$('3' * 32)/delete"; Check 'dispositivo inesistente -> 404' ($r.Code -eq 404)
foreach ($pc in @($pcW2, $pcR)) {
    $s = Sync $pc $rev
    $t1 = Tomb $s.Obj.devices 'id' $d1; $t2 = Tomb $s.Obj.fcreds 'id' $f1
    Check "$($pc.Name) riceve le lapidi (versione +1, password vuota)" ($t1 -and $t1.version -eq ($v1 + 1) -and @($t1.users).Count -eq 0 -and $t2 -and $t2.version -eq ($v2 + 1) -and $t2.password -eq '') $s.Text
}
Check 'registro: device-deleted e fcred-deleted senza PC, con cartella e nome' ((Sql "SELECT detail FROM events WHERE kind='device-deleted' AND pc_id IS NULL") -eq 'Solo / Da togliere' -and (Sql "SELECT detail FROM events WHERE kind='fcred-deleted' AND pc_id IS NULL") -eq 'Solo / dismesso')
$ev = Call GET '/api/events?kind=folder-renamed' -cookies $true
Check 'registro della console: l''evento di rinomina e'' leggibile' ($ev.Code -eq 200 -and $ev.Obj.total -eq 1 -and $ev.Obj.items[0].detail -eq 'Admin/Area -> Admin/Zona')
$evAll = Sql "SELECT group_concat(detail, '|') FROM events"
Check 'nessuna password nel registro eventi' (-not ($evAll.Contains($pwA) -or $evAll.Contains($pwB)))

'--- elenchi dopo le cancellazioni'
$gd = Call GET '/api/devices' -cookies $true; $gc = Call GET '/api/fcreds' -cookies $true
Check 'dispositivi e credenziali eliminati non sono piu in elenco' (-not (@($gd.Obj) | Where-Object { $_.id -in @($d1, $dA, $dB) }) -and -not (@($gc.Obj) | Where-Object { $_.id -in @($f1, $fA, $fB) }))
Check 'cartelle cancellate non sono piu in elenco' (-not (FolderRow 'Admin/Zona') -and -not (FolderRow 'Admin/Area/Sub') -and (FolderRow 'Occupata'))

Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
"=== PASS: $script:pass   FAIL: $script:fail"
