# Prova a DUE PC su questo computer, tutto nascosto: un server Teams di prova + due copie dell'app (A scrive, B sola lettura), ognuna con la sua cartella e il suo database.
# Controlla che cartelle (anche vuota), credenziali di cartella e password dei dispositivi arrivino dall'una all'altra, che la sola lettura non elimini,
# che chi non e' in sola lettura elimini, e che la connessione col segno "non sincronizzare" non arrivi al server.
# Uso: .\due-pc.ps1 -Build <cartella con Lemora.exe compilato in Release> [-Exe <Lemora.Server.exe>]
param([int]$Port = 18641, [string]$Exe = '', [string]$Build = '', [string]$Dest = '')
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$exe = if ($Exe) { $Exe } else { Join-Path $root 'Server\bin\Release\net10.0\Lemora.Server.exe' }
if (-not $Build) { 'Indica -Build <cartella con Lemora.exe compilato>'; return }
if (-not $Dest) { $Dest = Join-Path $root 'ui-map\_run\due-pc' }
# un server di una prova interrotta prima (stessa porta) si chiude
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
$work = Join-Path $env:TEMP ('due-pc-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$base = "https://127.0.0.1:$Port"
$appLog = Join-Path $env:TEMP 'due-pc-result.log'
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
function Prepare-Run($name) {
    $run = Join-Path $Dest $name
    if (Test-Path $run) { Remove-Item $run -Recurse -Force }
    robocopy $Build $run /E /XF Lemora.db Lemora.json /NFL /NDL /NJH /NJS /NP | Out-Null
    Set-Content (Join-Path $run 'Lemora.json') (@{ General = @{ CurrentLanguageCode = 'it-it' }; SqliteDatabasePath = (Join-Path $run 'demo-twopc.db') } | ConvertTo-Json) -Encoding UTF8
    Set-Content (Join-Path $run 'FORCE_INTO_PORTABLE_MODE') ''
    $run
}
function Start-App($run, $role, $code) {
    $env:LEMORA_INSTANCE_ID = "twopc$role"; $env:LEMORA_HEADLESS = '1'; $env:LEMORA_DEMO = 'teams-twopc'; $env:LEMORA_NO_HOTKEY = '1'; $env:LEMORA_NO_INSTALLER = '1'; $env:LEMORA_TEAMS_DEV = '1'
    $env:LEMORA_TWOPC_ROLE = $role; $env:LEMORA_TEAMS_SERVER = "127.0.0.1:$Port"; $env:LEMORA_TEAMS_CODE = $code; $env:LEMORA_TEAMS_LOG = $appLog
    $p = Start-Process (Join-Path $run 'Lemora.exe') -WorkingDirectory $run -WindowStyle Hidden -PassThru
    Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_DEMO, Env:LEMORA_NO_HOTKEY, Env:LEMORA_NO_INSTALLER, Env:LEMORA_TEAMS_DEV, Env:LEMORA_TWOPC_ROLE, Env:LEMORA_TEAMS_SERVER, Env:LEMORA_TEAMS_CODE, Env:LEMORA_TEAMS_LOG
    $p
}

$runA = Prepare-Run 'A'; $runB = Prepare-Run 'B'
$proc = Start-Process $exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Call GET '/api/state').Code -eq 200) { break } }
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
[void](Call POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress))
[void](Call POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = 'Password-di-prova-123'; port = $Port; address = 'all' } | ConvertTo-Json -Compress))
Start-Sleep 1
[void](Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}')

[IO.File]::Delete($appLog); foreach ($r in 'A', 'B') { foreach ($s in 'go1', 'go2', 'go3') { [IO.File]::Delete("$appLog.$r.$s") } }
$codeA = (Call POST '/api/pairing/new' '{}').Obj.code
$appA = Start-App $runA 'A' $codeA
$l = Wait-Log 'TWOPC A pending' 90
Check 'PC A: richiesta di abbinamento partita' ($l -ne '') 'nessun log'
$pcA = @((Call GET '/api/pcs').Obj) | Select-Object -First 1
[void](Call POST "/api/pcs/$($pcA.id)/approve" '{"permission":"write"}')
$codeB = (Call POST '/api/pairing/new' '{}').Obj.code
$appB = Start-App $runB 'B' $codeB
$l = Wait-Log 'TWOPC B pending' 90
$pcB = @((Call GET '/api/pcs').Obj) | Where-Object { $_.id -ne $pcA.id } | Select-Object -First 1
[void](Call POST "/api/pcs/$($pcB.id)/approve" '{"permission":"read"}')

'--- 1. A crea cartelle, credenziale, NAS e due connessioni (una "non sincronizzare")'
$a1 = Wait-Log 'TWOPC A a1 ' 120
Check 'A: NAS salvato sul server' ($a1 -match "nas='ok'") $a1
$conns = @((Call GET '/api/connections').Obj)
Check 'sul server c''e'' "Server Sede" e NON c''e'' "Casa mia"' (($conns | Where-Object { $_.name -eq 'Server Sede' }) -and -not ($conns | Where-Object { $_.name -eq 'Casa mia' })) ($conns | ConvertTo-Json -Compress -Depth 3)
Set-Content "$appLog.B.go1" 'go'
$b1 = Wait-Log 'TWOPC B b1 ' 120
Check 'B (sola lettura) riceve la cartella vuota "Prova / Sede / Vuota"' ($b1 -match 'Prova / Sede / Vuota') $b1
Check 'B riceve il NAS con utente e password' ($b1 -match 'NAS@Prova / Sede\|10\.20\.0\.40\|admin:Nas-1') $b1
Check 'B riceve la credenziale di cartella' ($b1 -match 'Prova / Sede\|sede\\admin') $b1
Check 'B riceve "Server Sede" come voce Teams e non "Casa mia"' ($b1 -match 'Server Sede\[Prova/Sede\]teams' -and $b1 -notmatch 'Casa mia') $b1

'--- 2. B (sola lettura) crea, prova a eliminare'
$b2 = Wait-Log 'TWOPC B b2 ' 120
Check 'B: crea lo Switch (ammesso alla sola lettura)' ($b2 -match "switch='ok'") $b2
Check 'B: NON elimina il NAS (sola lettura)' ($b2 -notmatch "elimina_nas='ok'" -and $b2 -match "elimina_nas='[^']+'") $b2
Check 'B: la cartella con un server dentro resta' ($b2 -match 'cartelle=\[[^\]]*Prova / Sede[,\]]') $b2
Check 'B: la cartella vuota si elimina' ($b2 -match 'elimina_vuota=True' -and $b2 -notmatch 'cartelle=\[[^\]]*Vuota') $b2
Set-Content "$appLog.A.go2" 'go'
$a2 = Wait-Log 'TWOPC A a2 ' 120
Check 'A riceve lo Switch di B' ($a2 -match 'Switch@Prova / Sede\|10\.20\.0\.2\|cisco:Sw-1') $a2
Check 'A vede il NAS ancora li'' e la cartella vuota sparita' ($a2 -match 'NAS@Prova / Sede' -and $a2 -notmatch 'Vuota') $a2

'--- 3. A elimina il NAS'
$a3 = Wait-Log 'TWOPC A a3 ' 120
Check 'A: elimina il NAS' ($a3 -match "elimina_nas='ok'" -and $a3 -notmatch 'NAS@') $a3
Set-Content "$appLog.B.go3" 'go'
$b3 = Wait-Log 'TWOPC B b3 ' 120
Check 'B: il NAS sparisce, lo Switch resta' ($b3 -notmatch 'NAS@' -and $b3 -match 'Switch@') $b3

Start-Sleep 2
foreach ($a in $appA, $appB) { if (-not $a.HasExited) { Stop-Process -Id $a.Id -Force -ErrorAction SilentlyContinue } }
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
'--- righe del log delle app'
Get-Content $appLog -Encoding UTF8
"=== PASS: $script:pass   FAIL: $script:fail"
