# Prova del client vero: l'app (istanza isolata e nascosta, ui-map\_run\restyle) fa l'abbinamento con un server vero come farebbe l'utente.
# Prima compilare l'app in ui-map\_build_pkgs e copiarla in ui-map\_run\restyle (vedi la memoria "aggiornare l'app di prova"); poi:  .\teams-live.ps1
# -Exe / -Run / -Instance: server, app e istanza diversi da quelli predefiniti (per non toccare build e istanze in uso)
param([int]$Port = 18472, [string]$Exe = '', [string]$Run = '', [string]$Instance = 'restyle')
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$exe = if ($Exe) { $Exe } else { Join-Path $root 'Server\bin\Release\net10.0\Lemora.Server.exe' }
$run = if ($Run) { $Run } else { Join-Path $root 'ui-map\_run\restyle' }
$work = Join-Path $env:TEMP ('pairing-test-live-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$base = "https://127.0.0.1:$Port"
$appLog = Join-Path $env:TEMP 'teams-live-result.log' # scritto dalla demo dell'app riga per riga (il log normale dell'app si scrive a blocchi)
$script:pass = 0; $script:fail = 0

function Call($method, $path, $json = $null, [bool]$cookies = $false) {
    $a = @('-k', '-s', '-X', $method, '-w', '\n%{http_code}', '-H', 'X-Lemora: 1')
    if ($cookies) { $a += @('-b', $jar, '-c', $jar) }
    if ($json -ne $null) { Set-Content -Path $body -Value $json -NoNewline -Encoding ascii; $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$body") }
    $a += "$base$path"
    $out = (& curl.exe @a) -join "`n"
    $i = $out.LastIndexOf("`n")
    $text = if ($i -ge 0) { $out.Substring(0, $i) } else { '' }
    $obj = $null; try { if ($text) { $obj = $text | ConvertFrom-Json } } catch { }
    [pscustomobject]@{ Code = [int]$out.Substring($i + 1); Obj = $obj; Text = $text }
}
function Check($label, $cond, $detail = '') { if ($cond) { $script:pass++; "PASS  $label" } else { $script:fail++; "FAIL  $label   $detail" } }
function Wait-Log($pattern, $seconds) {
    for ($i = 0; $i -lt $seconds; $i++) {
        if (Test-Path $appLog) { $m = Get-Content $appLog | Where-Object { $_ -match $pattern } | Select-Object -Last 1; if ($m) { return $m } }
        Start-Sleep 1
    }
    return ''
}

$proc = Start-Process $exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Call GET '/api/state').Code -eq 200) { break } }
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
[void](Call POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress) $true)
[void](Call POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = 'Password-di-prova-123'; port = $Port; address = 'all' } | ConvertTo-Json -Compress) $true)
Start-Sleep 1
[void](Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}' $true)

'--- annulla richiesta (un PC ritira la richiesta in attesa)'
$c0 = (Call POST '/api/pairing/new' '{}' $true).Obj.code
$rq = Call POST '/api/pair/request' (@{ code = $c0; pc = 'PC-ANNULLA'; user = 'prova' } | ConvertTo-Json -Compress)
Check 'richiesta di prova partita' ($rq.Code -eq 200 -and $rq.Obj.id) $rq.Text
$bad = Call POST '/api/pair/cancel' (@{ id = $rq.Obj.id; token = ('0' * 64) } | ConvertTo-Json -Compress)
Check 'annullare con un segreto sbagliato non funziona' ($bad.Code -eq 404) $bad.Text
Check 'la richiesta e'' ancora in console' (@((Call GET '/api/pcs' $null $true).Obj | Where-Object { $_.pc -eq 'PC-ANNULLA' }).Count -eq 1)
$ok = Call POST '/api/pair/cancel' (@{ id = $rq.Obj.id; token = $rq.Obj.token } | ConvertTo-Json -Compress)
Check 'annullare con il segreto giusto funziona' ($ok.Code -eq 200) $ok.Text
Check 'la richiesta e'' sparita dalla console' (@((Call GET '/api/pcs' $null $true).Obj | Where-Object { $_.pc -eq 'PC-ANNULLA' }).Count -eq 0)
$again = Call POST '/api/pair/cancel' (@{ id = $rq.Obj.id; token = $rq.Obj.token } | ConvertTo-Json -Compress)
Check 'annullare due volte da 404' ($again.Code -eq 404) $again.Text

$code = (Call POST '/api/pairing/new' '{}' $true).Obj.code

# voci condivise create prima dell'abbinamento
$idA = (Call POST '/api/connections' '{"folder":"Uffici/Roma","name":"Server Roma","protocol":"rdp","host":"10.1.2.3","port":3389,"user":"admin","password":"pw-roma-1","tags":"web,prod"}' $true).Obj.id
$idB = (Call POST '/api/connections' '{"folder":"","name":"Linux build","protocol":"ssh","host":"10.1.2.4","port":22,"user":"root","password":"pw-linux-1","tags":""}' $true).Obj.id
$rC = Call POST '/api/connections' '{"folder":"Uffici/Roma","name":"VNC Ufficio","protocol":"vnc","host":"10.1.2.6","port":5900,"user":"","password":"pw-vnc-1","tags":""}' $true
Check 'il server accetta una voce VNC' ($rC.Code -eq 200 -and $rC.Obj.id) $rC.Text
$idC = $rC.Obj.id

[IO.File]::Delete($appLog); foreach ($s in '.go', '.go2', '.go3', '.go4') { [IO.File]::Delete("$appLog$s") } # niente righe vecchie (File.Delete non fallisce se il file non c'e')
$env:LEMORA_INSTANCE_ID = $Instance; $env:LEMORA_HEADLESS = '1'; $env:LEMORA_DEMO = 'teams-live'; $env:LEMORA_NO_HOTKEY = '1'
$env:LEMORA_TEAMS_SERVER = "127.0.0.1:$Port"; $env:LEMORA_TEAMS_CODE = $code; $env:LEMORA_TEAMS_LOG = $appLog
$app = Start-Process (Join-Path $run 'Lemora.exe') -WorkingDirectory $run -RedirectStandardOutput (Join-Path $env:TEMP 'app_teams-live.log') -RedirectStandardError (Join-Path $env:TEMP 'app_teams-live.err') -WindowStyle Hidden -PassThru
Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_DEMO, Env:LEMORA_NO_HOTKEY, Env:LEMORA_TEAMS_SERVER, Env:LEMORA_TEAMS_CODE, Env:LEMORA_TEAMS_LOG

'--- il PC invia la richiesta'
$l = Wait-Log 'DEMO teams-live: pending' 90
Check 'richiesta partita e salvata (id, segreto e impronta presenti)' ($l -match 'state=pending id=32 token=64 fp=64') $l
Check 'la finestra mostra "In attesa di approvazione" senza errori' ($l -match "titolo='In attesa di approvazione'" -and $l -match "nota=''") $l
$r = Call GET '/api/pcs' $null $true
$pc = @($r.Obj)[0]
Check 'il server vede PC e utente di Windows, indirizzo locale, richiesta in attesa' ($pc.state -eq 'pending' -and $pc.pc -eq $env:COMPUTERNAME -and $pc.user -eq $env:USERNAME -and $pc.address -eq '127.0.0.1') $r.Text

'--- l''amministratore approva (scrittura, non copiare)'
$r = Call POST "/api/pcs/$($pc.id)/approve" '{"permission":"write","noCopy":true}' $true
Check 'approvazione' ($r.Code -eq 200)
$l = Wait-Log 'DEMO teams-live: approved' 60
Check 'il client riceve la chiave e il permesso' ($l -match 'state=approved permission=write noCopy=True key=presente') $l
Check 'la finestra e'' passata da sola a "PC approvato"' ($l -match "titolo='PC approvato'") $l

'--- prima sincronizzazione delle voci condivise'
$l = Wait-Log 'DEMO teams-live: sync1' 60
Check 'origine Teams presente, scrivibile (permesso scrittura), con 3 voci' ($l -match 'origine=presente scrivibile=True voci=3') $l
Check 'VNC: arriva come voce VNC con nuvola, senza utente, password in chiaro al momento di collegarsi' ($l -match 'VNC Ufficio\[VNC\|Uffici/Roma\|10\.1\.2\.6:5900\|\|pw=pw-vnc-1\|tag=\]cloud=True') $l
Check 'RDP: cartella, indirizzo, utente, password in chiaro al momento di collegarsi, tag' ($l -match 'Server Roma\[RDP\|Uffici/Roma\|10\.1\.2\.3:3389\|admin\|pw=pw-roma-1\|tag=prod,web\]') $l
Check 'SSH senza cartella' ($l -match 'Linux build\[SSH\|\|10\.1\.2\.4:22\|root\|pw=pw-linux-1\|tag=\]') $l

'--- l''amministratore cambia una voce, ne cancella una, ne aggiunge una'
$r = Call POST "/api/connections/$idA/update" '{"folder":"Uffici/Roma","name":"Server Roma 2","protocol":"rdp","host":"10.1.2.30","port":3390,"user":"admin","password":"pw-roma-2","tags":"web"}' $true
Check 'modifica sul server' ($r.Code -eq 200) $r.Text
$r = Call POST "/api/connections/$idB/delete" '{}' $true
Check 'cancellazione sul server' ($r.Code -eq 200) $r.Text
$r = Call POST '/api/connections' '{"folder":"Milano","name":"Nuovo SSH","protocol":"ssh","host":"10.1.2.5","port":2222,"user":"u","password":"","tags":""}' $true
Check 'aggiunta sul server' ($r.Code -eq 200) $r.Text
Set-Content "$appLog.go" 'go'
$l = Wait-Log 'DEMO teams-live: sync2' 60
Check 'seconda sincronizzazione: 3 voci (una modificata, una nuova, una tolta)' ($l -match 'voci=3 ') $l
Check 'la voce VNC del server e'' nell''elenco della Home con la nuvola' ($l -match 'home_vnc_cloud=\[VNC Ufficio\]') $l
Check 'voce modificata aggiornata' ($l -match 'Server Roma 2\[RDP\|Uffici/Roma\|10\.1\.2\.30:3390\|admin\|pw=pw-roma-2\|tag=web\]') $l
Check 'voce cancellata sparita' ($l -notmatch 'Linux build') $l
Check 'voce nuova senza password' ($l -match 'Nuovo SSH\[SSH\|Milano\|10\.1\.2\.5:2222\|u\|pw=\|tag=\]') $l

function Names { @((Call GET '/api/connections' $null $true).Obj | ForEach-Object { $_.name }) }
'--- scrittura dal PC: permesso scrittura con "non copiare"'
$l = Wait-Log 'DEMO teams-live: wr1' 60
Check 'l''origine Teams e'' scrivibile' ($l -match 'scrivibile=True') $l
Check 'aggiungere e'' vietato da "non copiare" (messaggio chiaro)' ($l -match "aggiungi='.*non permette.* modifica='ok'") $l
Check 'modificare una voce condivisa e'' permesso' ($l -match "modifica='ok'") $l
Check 'cancellare una voce condivisa e'' permesso' ($l -match "cancella='ok'") $l
$n = Names
Check 'sul server: la voce e'' stata rinominata, quella cancellata non c''e'', quella vietata non c''e''' (($n -contains 'Nuovo SSH b') -and ($n -notcontains 'Server Roma 2') -and ($n -notcontains 'Vietata')) ($n -join ',')
Check 'il PC modifica la voce VNC (versione vista = quella del server)' ($l -match "modifica_vnc='ok'") $l
$vc = @((Call GET '/api/connections' $null $true).Obj | Where-Object { $_.id -eq $idC })[0]
Check 'sul server la VNC e'' rinominata, resta vnc, versione 2, senza utente, password tenuta' ($vc.name -eq 'VNC Ufficio b' -and $vc.protocol -eq 'vnc' -and $vc.version -eq 2 -and $vc.user -eq '' -and $vc.hasPassword) ($vc | ConvertTo-Json -Compress)

'--- l''amministratore toglie "non copiare": ora il PC puo'' aggiungere'
$r = Call POST "/api/pcs/$($pc.id)/update" '{"permission":"write","noCopy":false}' $true
Check 'aggiornamento permesso' ($r.Code -eq 200)
Set-Content "$appLog.go2" 'go'
$l = Wait-Log 'DEMO teams-live: wr2' 60
Check 'aggiungere ora funziona e la voce prende un id del server' ($l -match "aggiungi='ok' id=teams-\.\." -and $l -match 'Aggiunto da PC\[RDP\|\|10\.9\.9\.9:3389\|u\|pw=pw-add') $l
$all = (Call GET '/api/connections' $null $true).Obj
$added = @($all | Where-Object { $_.name -eq 'Aggiunto da PC' })
Check 'sul server c''e'' la voce aggiunta dal PC' ($added.Count -eq 1 -and $added[0].host -eq '10.9.9.9') ($all | ConvertTo-Json -Compress)
$rv = Call POST "/api/connections/$($added[0].id)/reveal" '{}' $true
Check 'la password della voce aggiunta e'' arrivata e il server la tiene cifrata' ($rv.Obj.password -eq 'pw-add') $rv.Text
$av = @($all | Where-Object { $_.name -eq 'VNC da PC' })
Check 'il PC aggiunge una voce VNC: sul server e'' vnc, porta 5901, senza utente' ($l -match "aggiungi_vnc='ok'" -and $av.Count -eq 1 -and $av[0].protocol -eq 'vnc' -and $av[0].port -eq 5901 -and $av[0].user -eq '') $l
$rv = Call POST "/api/connections/$($av[0].id)/reveal" '{}' $true
Check 'password VNC arrivata e tenuta cifrata dal server' ($rv.Obj.password -eq 'pw-vnc-pc') $rv.Text
'--- l''amministratore disabilita una VNC e ne elimina un''altra'
$r = Call POST "/api/connections/$idC/disable" '{"disabled":true}' $true
Check 'VNC disabilitata' ($r.Code -eq 200) $r.Text
$r = Call POST "/api/connections/$($av[0].id)/delete" '{}' $true
Check 'VNC eliminata (cestino)' ($r.Code -eq 200) $r.Text

'--- conflitto: la stessa voce cambia sul server mentre il PC ha una copia vecchia'
$nb = @($all | Where-Object { $_.name -eq 'Nuovo SSH b' })[0]
$r = Call POST "/api/connections/$($nb.id)/update" '{"folder":"Milano","name":"Nuovo SSH admin","protocol":"ssh","host":"10.1.2.5","port":2222,"user":"u","password":null,"tags":""}' $true
Check 'modifica dell''amministratore' ($r.Code -eq 200) $r.Text
Set-Content "$appLog.go3" 'go'
$l = Wait-Log 'DEMO teams-live: wr3' 60
Check 'la modifica fatta su una copia vecchia viene rifiutata con un messaggio chiaro' ($l -match "modifica_su_copia_vecchia='[^']*cambiata sul server[^']*'") $l
Check 'sul server resta la modifica dell''amministratore' ((Names) -contains 'Nuovo SSH admin' -and (Names) -notcontains 'Nuovo SSH c')
Check 'sul PC la VNC disabilitata e quella eliminata spariscono (anche dalla Home)' ($l -notmatch 'VNC Ufficio' -and $l -notmatch 'VNC da PC' -and $l -match 'home_vnc_cloud=\[\]') $l

'--- l''amministratore passa il PC a sola lettura'
$r = Call POST "/api/pcs/$($pc.id)/update" '{"permission":"read","noCopy":false}' $true
Check 'aggiornamento permesso' ($r.Code -eq 200)
Set-Content "$appLog.go4" 'go'
$l = Wait-Log 'DEMO teams-live: wr4' 60
Check 'l''origine Teams torna in sola lettura' ($l -match 'scrivibile=False') $l

'--- l''amministratore scollega il PC'
$r = Call POST "/api/pcs/$($pc.id)/revoke" '{}' $true
Check 'scollegamento' ($r.Code -eq 200)
$l = Wait-Log 'DEMO teams-live: revoked' 100
Check 'il client se ne accorge, cancella la chiave e lo dice nella barra' ($l -match 'state=revoked key=cancellata' -and $l -match "barra='Teams · scollegato dal server'") $l

$l = Wait-Log 'DEMO teams-live: sync3' 30
Check 'dopo lo scollegamento la copia delle voci e l''origine Teams spariscono' ($l -match 'origine=assente') $l

Start-Sleep 2
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
'--- righe del log dell''app'
Get-Content $appLog
"=== PASS: $script:pass   FAIL: $script:fail"
