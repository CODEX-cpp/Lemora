# PC non installato in modalita' Teams (portatile di prova, SENZA LEMORA_TEAMS_DEV) che si abbina dopo da Opzioni > Generale (demo teams-localjoin).
# L'amministratore approva con permesso scrittura: senza riavviare devono partire barra, sincronizzazione, voci condivise e sezione condivisione.
# Poi si riavvia l'app: deve ripartire da sola come PC Teams. Foto in ui-map\_shots_tmp\teams-localjoin-*.png
# Uso: .\teams-localjoin.ps1 -Run <cartella con Lemora.exe compilato> [-Exe <Lemora.Server.exe>]
param([int]$Port = 18661, [string]$Exe = '', [string]$Run = '', [string]$Instance = 'localjointest')
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$exe = if ($Exe) { $Exe } else { Join-Path $root 'Server\bin\Release\net10.0\Lemora.Server.exe' }
$run = if ($Run) { $Run } else { Join-Path $root 'ui-map\_run\restyle' }
$work = Join-Path $env:TEMP ('pairing-test-localjoin-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$base = "https://127.0.0.1:$Port"
$appLog = Join-Path $env:TEMP 'teams-localjoin-result.log'
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
function Start-App {
    $env:LEMORA_INSTANCE_ID = $Instance; $env:LEMORA_HEADLESS = '1'; $env:LEMORA_DEMO = 'teams-localjoin'; $env:LEMORA_NO_HOTKEY = '1'; $env:LEMORA_NO_INSTALLER = '1'
    $env:LEMORA_TEAMS_SERVER = "127.0.0.1:$Port"; $env:LEMORA_TEAMS_CODE = $code; $env:LEMORA_TEAMS_LOG = $appLog
    $p = Start-Process (Join-Path $run 'Lemora.exe') -WorkingDirectory $run -WindowStyle Hidden -PassThru
    Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_DEMO, Env:LEMORA_NO_HOTKEY, Env:LEMORA_NO_INSTALLER, Env:LEMORA_TEAMS_SERVER, Env:LEMORA_TEAMS_CODE, Env:LEMORA_TEAMS_LOG
    $p
}

Remove-Item Env:LEMORA_TEAMS_DEV -ErrorAction SilentlyContinue # deve essere un PC NON Teams
$proc = Start-Process $exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Call GET '/api/state').Code -eq 200) { break } }
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
[void](Call POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress))
[void](Call POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = 'Password-di-prova-123'; port = $Port; address = 'all' } | ConvertTo-Json -Compress))
Start-Sleep 1
[void](Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}')
[void](Call POST '/api/connections' '{"folder":"Roma","name":"Voce A","protocol":"rdp","host":"10.0.0.1","port":3389,"user":"admin","password":"pw-a","tags":""}')
[void](Call POST '/api/connections' '{"folder":"","name":"Voce B","protocol":"ssh","host":"10.0.0.2","port":22,"user":"root","password":"pw-b","tags":""}')
$code = (Call POST '/api/pairing/new' '{}').Obj.code

[IO.File]::Delete($appLog)
Set-Content (Join-Path $run 'FORCE_INTO_PORTABLE_MODE') '' # dati accanto all'exe di prova: portatile, non installato in modalita' Teams
# profilo e database SOLO di prova nella cartella -Run (mai il database vero dell'utente): si riparte da "non collegato"
$testDb = Join-Path $run 'localjoin-test.db'
Remove-Item "$testDb*" -Force -ErrorAction SilentlyContinue
Set-Content (Join-Path $run 'Lemora.json') ('{ "General": { "CurrentLanguageCode": "it-it" }, "SqliteDatabasePath": ' + (ConvertTo-Json $testDb) + ' }') -Encoding ascii
$app = Start-App

'--- prima: PC non Teams'
$l = Wait-Log 'teams-localjoin: (prima|riavvio|errore)' 90
Check 'prima dell''abbinamento non e'' Teams, niente barra ne'' sezione' ($l -match 'prima isTeams=False' -and $l -match 'barra=Collapsed' -and $l -match 'sezione_condivisione=False' -and $l -match 'voce_collega=True') $l
$l = Wait-Log 'teams-localjoin: (pending|errore)' 60
Check 'richiesta partita da Opzioni' ($l -match 'pending isTeams=True') $l
$pc = @((Call GET '/api/pcs').Obj)[0]
$r = Call POST "/api/pcs/$($pc.id)/approve" '{"permission":"write"}'
Check 'approvato con permesso scrittura' ($r.Code -eq 200) $r.Text

'--- dopo l''approvazione, senza riavviare'
$l = Wait-Log 'teams-localjoin: (dopo|errore)' 120
Check 'ora conta come Teams' ($l -match 'dopo isTeams=True stato=approved permesso=write') $l
Check 'barra in basso visibile' ($l -match 'barra=Visible') $l
Check 'voci condivise scaricate (2) e scrivibili' ($l -match 'origine_teams=voci=2 scrivibile=True') $l
Check 'voci condivise nella Home' ($l -match 'home_teams=\[Voce A,Voce B\]') $l
Check 'sezione e interruttore di condivisione visibili, voce Collega nascosta' ($l -match 'sezione_condivisione=True interruttore=True voce_collega=False') $l

'--- riavvio'
Start-Sleep 2
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep 2
$app = Start-App
$l = Wait-Log 'teams-localjoin: (riavvio|errore)' 90
Check 'al riavvio riparte da solo come Teams (barra, voci)' ($l -match 'riavvio isTeams=True' -and $l -match 'barra=Visible' -and $l -match 'origine_teams=voci=2') $l

Start-Sleep 2
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
Remove-Item "$testDb*" -Force -ErrorAction SilentlyContinue
'--- righe del log dell''app'
Get-Content $appLog -Encoding UTF8
"=== PASS: $script:pass   FAIL: $script:fail"
