# Prova del client vero con il permesso "Richieste": l'app (istanza isolata e nascosta, demo teams-requests) contro un server di prova a parte.
# Il client chiede modifica, eliminazione e nuova voce; qui l'amministratore approva/rifiuta via API e si guarda cosa vede il client. Foto in ui-map\_shots_tmp\teams-req-*.png
# Uso: .\teams-requests.ps1 -Exe <Lemora.Server.exe> -Run <cartella con Lemora.exe compilato>   (la cartella -Run riceve FORCE_INTO_PORTABLE_MODE: dati accanto all'exe)
# ATTENZIONE: senza -Run si usa una copia isolata in %TEMP%, MAI ui-map\_run\restyle: la sua Lemora.json punta al database vero dell'utente.
param([int]$Port = 18612, [string]$Exe = '', [string]$Run = '', [string]$Instance = 'reqtest')
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$exe = if ($Exe) { $Exe } else { Join-Path $root 'Server\bin\Release\net10.0\Lemora.Server.exe' }
if ($Run) { $run = $Run } else {
    $run = Join-Path $env:TEMP ('teams-requests-run-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $run | Out-Null
    Copy-Item (Join-Path $root 'ui-map\_build_new\*') $run -Recurse -Force -ErrorAction SilentlyContinue
}
$work = Join-Path $env:TEMP ('pairing-test-live-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$base = "https://127.0.0.1:$Port"
$appLog = Join-Path $env:TEMP 'teams-requests-result.log'
$script:pass = 0; $script:fail = 0

function Call($method, $path, $json = $null) {
    $a = @('-k', '-s', '-X', $method, '-w', '\n%{http_code}', '-H', 'X-Lemora: 1', '-b', $jar, '-c', $jar)
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
        if (Test-Path $appLog) { $m = Get-Content $appLog -Encoding UTF8 | Where-Object { $_ -match $pattern } | Select-Object -Last 1; if ($m) { return $m } }
        Start-Sleep 1
    }
    return ''
}

$proc = Start-Process $exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Call GET '/api/state').Code -eq 200) { break } }
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
[void](Call POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress))
[void](Call POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = 'Password-di-prova-123'; port = $Port; address = 'all' } | ConvertTo-Json -Compress))
Start-Sleep 1
[void](Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}')
$idA = (Call POST '/api/connections' '{"folder":"Roma","name":"Voce A","protocol":"rdp","host":"10.0.0.1","port":3389,"user":"admin","password":"pw-a","tags":""}').Obj.id
$idB = (Call POST '/api/connections' '{"folder":"","name":"Voce B","protocol":"ssh","host":"10.0.0.2","port":22,"user":"root","password":"pw-b","tags":""}').Obj.id
$code = (Call POST '/api/pairing/new' '{}').Obj.code

[IO.File]::Delete($appLog); foreach ($s in '.go', '.go2') { [IO.File]::Delete("$appLog$s") }
Set-Content (Join-Path $run 'FORCE_INTO_PORTABLE_MODE') '' # dati accanto all'exe di prova: niente scelta di installazione, niente AppData
$env:LEMORA_INSTANCE_ID = $Instance; $env:LEMORA_HEADLESS = '1'; $env:LEMORA_DEMO = 'teams-requests'; $env:LEMORA_NO_HOTKEY = '1'; $env:LEMORA_NO_INSTALLER = '1'; $env:LEMORA_TEAMS_DEV = '1'
$env:LEMORA_TEAMS_SERVER = "127.0.0.1:$Port"; $env:LEMORA_TEAMS_CODE = $code; $env:LEMORA_TEAMS_LOG = $appLog
$app = Start-Process (Join-Path $run 'Lemora.exe') -WorkingDirectory $run -WindowStyle Hidden -PassThru
Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_DEMO, Env:LEMORA_NO_HOTKEY, Env:LEMORA_NO_INSTALLER, Env:LEMORA_TEAMS_DEV, Env:LEMORA_TEAMS_SERVER, Env:LEMORA_TEAMS_CODE, Env:LEMORA_TEAMS_LOG

'--- abbinamento con permesso Richieste'
$l = Wait-Log 'teams-requests: (pending|errore)' 90
Check 'richiesta di abbinamento partita' ($l -match 'pending') $l
$pc = @((Call GET '/api/pcs').Obj)[0]
$r = Call POST "/api/pcs/$($pc.id)/approve" '{"permission":"requests"}'
Check 'approvato con permesso requests' ($r.Code -eq 200) $r.Text
$l = Wait-Log 'teams-requests: (req0|errore)' 90
Check 'il client sa il permesso: requests' ($l -match 'permesso=requests') $l
Check 'Modifica ed Elimina restano disponibili' ($l -match 'scrivibile=True' -and $l -match 'elimina=True') $l
Check 'conferma di Elimina: "Chiedi all''amministratore..."' ($l -match "conferma='Chiedi all'amministratore di eliminare questa connessione per tutti\?'") $l
Check 'all''inizio nessuna voce in attesa' ($l -match 'Voce A\[10\.0\.0\.1\]attesa=False' -and $l -match 'Voce B\[10\.0\.0\.2\]attesa=False') $l

'--- il client chiede modifica, eliminazione e nuova voce'
$l = Wait-Log 'teams-requests: (req1|errore)' 60
Check 'Salva / Elimina / Nuova voce riescono (diventano richieste)' ($l -match "modifica='ok' elimina='ok' nuova='ok'") $l
Check 'avviso discreto "Richiesta ... inviata all''amministratore"' ($l -match 'avvisi=\[.*inviata all.amministratore') $l
Check 'la voce locale resta com''era, con l''etichetta in attesa' ($l -match 'Voce A\[10\.0\.0\.1\]attesa=True') $l
Check 'la voce da eliminare resta, in attesa' ($l -match 'Voce B\[10\.0\.0\.2\]attesa=True') $l
Check 'la nuova voce non compare prima dell''approvazione' ($l -notmatch 'Voce chiesta') $l
$reqs = @((Call GET '/api/requests').Obj)
Check 'sul server 3 richieste in attesa (update, delete, create)' (@($reqs | Where-Object { $_.state -eq 'pending' }).Count -eq 3 -and ($reqs.kind -join ',') -match 'update' -and ($reqs.kind -join ',') -match 'delete' -and ($reqs.kind -join ',') -match 'create') ($reqs | ConvertTo-Json -Compress -Depth 4)
$qU = ($reqs | Where-Object { $_.kind -eq 'update' }).id; $qD = ($reqs | Where-Object { $_.kind -eq 'delete' }).id; $qC = ($reqs | Where-Object { $_.kind -eq 'create' }).id
Check 'la modifica chiesta e'' quella giusta (host 10.5.5.55, password invariata)' (($reqs | Where-Object { $_.kind -eq 'update' }).new.host -eq '10.5.5.55' -and ($reqs | Where-Object { $_.kind -eq 'update' }).pwChanged -eq $false)
Check 'sul server la voce non e'' cambiata' ((@((Call GET '/api/connections').Obj) | Where-Object { $_.id -eq $idA }).host -eq '10.0.0.1')

'--- l''amministratore decide'
[void](Call POST "/api/requests/$qU/approve" '{}')
[void](Call POST "/api/requests/$qC/approve" '{}')
[void](Call POST "/api/requests/$qD/reject" '{"note":"serve ancora"}')
Set-Content "$appLog.go" 'go'
$l = Wait-Log 'teams-requests: (req2|errore)' 60
Check 'approvata: la voce si aggiorna da sola e l''orologio sparisce' ($l -match 'Voce A\[10\.5\.5\.55\]attesa=False') $l
Check 'approvata la nuova voce: compare' ($l -match 'Voce chiesta\[10\.5\.5\.77\]attesa=False') $l
Check 'rifiutata: la voce resta, senza orologio' ($l -match 'Voce B\[10\.0\.0\.2\]attesa=False') $l
Check 'rifiutata: avviso con la nota dell''amministratore' ($l -match 'rifiutata.*Voce B.*serve ancora') $l

'--- sola lettura'
[void](Call POST "/api/pcs/$($pc.id)/update" '{"permission":"read"}')
Set-Content "$appLog.go2" 'go'
$l = Wait-Log 'teams-requests: (req3|errore)' 60
Check 'passato a sola lettura: Modifica/Elimina tornano nascoste' ($l -match 'scrivibile=False permesso=read') $l

Start-Sleep 2
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
'--- righe del log dell''app'
Get-Content $appLog -Encoding UTF8
"=== PASS: $script:pass   FAIL: $script:fail"
