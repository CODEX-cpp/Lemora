# Prova del client vero con le connessioni personali (salvate sul PC) verso il server: l'app (istanza isolata e nascosta, demo teams-personal) contro un server di prova a parte.
# Sola lettura -> invio automatico dichiarato, approva/rifiuta, voce locale sostituita; Richieste -> interruttore acceso di default, spento = copie cancellate;
# Scrittura -> pubblicata subito, "Salva in" Teams; voce Teams eliminata -> la locale ricompare. Foto in ui-map\_shots_tmp\teams-pers-*.png
# Uso: .\teams-personal.ps1 -Exe <Lemora.Server.exe> -Run <cartella con Lemora.exe compilato>   (la cartella -Run riceve FORCE_INTO_PORTABLE_MODE: dati accanto all'exe)
param([int]$Port = 18631, [string]$Exe = '', [string]$Run = '', [string]$Instance = 'perstest')
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$exe = if ($Exe) { $Exe } else { Join-Path $root 'Server\bin\Release\net10.0\Lemora.Server.exe' }
# la demo aggiunge voci locali e abbina il PC: serve una cartella di prova con il SUO database (mai quello vero in AppData, come in ui-map\_run\restyle)
if (-not $Run) { 'Indica -Run <cartella di prova con Lemora.exe compilato>'; return }
$run = $Run
$profile1 = Join-Path $run 'Lemora.json'
if (-not (Test-Path $profile1)) {
    Set-Content $profile1 (@{ General = @{ CurrentLanguageCode = 'it-it' }; SqliteDatabasePath = (Join-Path $run 'demo-personal.db') } | ConvertTo-Json) -Encoding UTF8
} elseif (-not ((Get-Content $profile1 -Raw | ConvertFrom-Json).SqliteDatabasePath -like "$run*")) { "Il database di $profile1 non sta in $run`: rifiuto (rischio di toccare dati veri)"; return }
$work = Join-Path $env:TEMP ('pairing-test-live-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$base = "https://127.0.0.1:$Port"
$appLog = Join-Path $env:TEMP 'teams-personal-result.log'
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
function Mine($pcId) { @((Call GET '/api/personal').Obj) | Where-Object { $_.pcId -eq $pcId } }

$proc = Start-Process $exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Call GET '/api/state').Code -eq 200) { break } }
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
[void](Call POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress))
[void](Call POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = 'Password-di-prova-123'; port = $Port; address = 'all' } | ConvertTo-Json -Compress))
Start-Sleep 1
[void](Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}')
$code = (Call POST '/api/pairing/new' '{}').Obj.code

[IO.File]::Delete($appLog); foreach ($s in '.go', '.go2', '.go3', '.go4') { [IO.File]::Delete("$appLog$s") }
Set-Content (Join-Path $run 'FORCE_INTO_PORTABLE_MODE') '' # dati accanto all'exe di prova: niente scelta di installazione, niente AppData
$env:LEMORA_INSTANCE_ID = $Instance; $env:LEMORA_HEADLESS = '1'; $env:LEMORA_DEMO = 'teams-personal'; $env:LEMORA_NO_HOTKEY = '1'; $env:LEMORA_NO_INSTALLER = '1'; $env:LEMORA_TEAMS_DEV = '1'
$env:LEMORA_TEAMS_SERVER = "127.0.0.1:$Port"; $env:LEMORA_TEAMS_CODE = $code; $env:LEMORA_TEAMS_LOG = $appLog
$app = Start-Process (Join-Path $run 'Lemora.exe') -WorkingDirectory $run -WindowStyle Hidden -PassThru
Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_DEMO, Env:LEMORA_NO_HOTKEY, Env:LEMORA_NO_INSTALLER, Env:LEMORA_TEAMS_DEV, Env:LEMORA_TEAMS_SERVER, Env:LEMORA_TEAMS_CODE, Env:LEMORA_TEAMS_LOG

'--- sola lettura: invio automatico'
$l = Wait-Log 'teams-personal: (pending|errore)' 90
Check 'abbinamento partito, interruttore acceso di default' ($l -match 'interruttore_predefinito=True') $l
$pc = @((Call GET '/api/pcs').Obj)[0]
[void](Call POST "/api/pcs/$($pc.id)/approve" '{"permission":"read"}')
$l = Wait-Log 'teams-personal: (p0 |errore)' 90
Check 'sola lettura: niente interruttore, il pannello non parla dell''invio' ($l -match 'permesso=read' -and $l -match 'interruttore=assente' -and $l -notmatch "pannello='[^']*(invia|condivis|sends|shared|Connessioni ricevute|ultima sincronizzazione)") $l
Check 'barra Teams: niente invio (solo "Teams - collegato - ora")' ($l -match "barra=''") $l # barra_vera qui no: la finestra principale non ha ancora riletto l'abbinamento fatto dalla demo
$l = Wait-Log 'teams-personal: (p0b|errore)' 60
Check 'orologio "in attesa" su Mia RDP e Mia SSH (proposte non decise), non su Mia SFTP' ($l -match 'orologio=\[[^\]]*Mia RDP' -and $l -match 'orologio=\[[^\]]*Mia SSH' -and $l -notmatch 'orologio=\[[^\]]*Mia SFTP') $l
$l = Wait-Log 'teams-personal: (p0c|errore)' 60
Check 'Opzioni, sola lettura: una sola riga breve, senza interruttore' ($l -match 'sezione=True riga_unica=True riga_completa=False interruttore=False' -and $l -match "testo='(Connessioni salvate: condivise con l.amministratore\.|Saved connections: shared with the administrator\.)'") $l
$m = @(Mine $pc.id)
Check 'sul server 2 proposte in attesa (RDP e SSH, non la SFTP), password presente ma nascosta' ($m.Count -eq 2 -and @($m | Where-Object { $_.state -eq 'pending' -and $_.hasPassword }).Count -eq 2 -and -not ($m | Where-Object { $_.name -eq 'Mia SFTP' })) ($m | ConvertTo-Json -Compress -Depth 4)
Check 'cartella e indirizzo arrivati giusti' (($m | Where-Object { $_.name -eq 'Mia RDP' }).folder -eq 'Mie' -and ($m | Where-Object { $_.name -eq 'Mia RDP' }).host -eq '10.7.7.1')

'--- l''amministratore approva e rifiuta'
[void](Call POST "/api/personal/$(($m | Where-Object { $_.name -eq 'Mia RDP' }).id)/approve" '{"note":"ok team"}')
[void](Call POST "/api/personal/$(($m | Where-Object { $_.name -eq 'Mia SSH' }).id)/reject" '{"note":"tienila tu"}')
$shared = @((Call GET '/api/connections').Obj) | Where-Object { $_.name -eq 'Mia RDP' }
Set-Content "$appLog.go" 'go'
$l = Wait-Log 'teams-personal: (p1|errore)' 60
Check 'avviso di approvazione' ($l -match 'avvisi=\[.*Mia RDP') $l
Check 'avviso di rifiuto con la nota' ($l -match 'Mia SSH.*tienila tu') $l
Check 'Mia RDP una sola volta, come voce Teams (la locale e'' nascosta)' ($l -match 'Mia RDP\|teams' -and $l -notmatch 'Mia RDP\|locale') $l
Check 'Mia SSH rifiutata: resta locale' ($l -match 'Mia SSH\|locale') $l

'--- permesso Richieste'
[void](Call POST "/api/pcs/$($pc.id)/update" '{"permission":"requests"}')
Set-Content "$appLog.go2" 'go'
$l = Wait-Log 'teams-personal: (p2 |errore)' 60
Check 'interruttore presente e acceso di default' ($l -match 'permesso=requests' -and $l -match 'interruttore=acceso') $l
Check 'barra: propone le connessioni all''amministratore (ERRORI 116)' ($l -match "barra=' . (propone le connessioni salvate|proposes saved)") $l # barra_vera qui no: il permesso l'ha letto la demo, non il controllo della finestra principale
$l = Wait-Log 'teams-personal: (p2b|errore)' 60
Check 'Opzioni, richieste: sezione Teams completa con interruttore acceso' ($l -match 'sezione=True riga_unica=False riga_completa=True interruttore=True acceso=True') $l
$l = Wait-Log 'teams-personal: (p2c|errore)' 60
Check 'richieste, interruttore acceso: nell''editor nessuna scelta, la voce nuova va al server come richiesta' ($l -match 'editor_richieste_acceso salva_in=False manda_richiesta_visibile=False manda_richiesta=True scelta=1') $l
$l = Wait-Log 'teams-personal: (p3 |errore)' 60
Check 'spento: avviso discreto "restano sul server"' ($l -match 'avvisi=\[.*(restano sul server|stay on the server)') $l
Check 'interruttore spento dal pannello: salvato, barra senza dichiarazione' ($l -match 'interruttore_salvato=False' -and $l -match "barra=''") $l
Check 'scritta vera in basso aggiornata subito: niente "propone/condivide" (ERRORI 113)' ($l -match "barra_vera='[^']+'" -and $l -notmatch "barra_vera='[^']*(propone|condivide|invia|proposes|shares|sends)") $l
$l = Wait-Log 'teams-personal: (p3c|errore)' 60
Check 'richieste, interruttore spento: nell''editor compare "Manda al server come richiesta", acceso' ($l -match 'editor_richieste_spento salva_in=False manda_richiesta_visibile=True manda_richiesta=True scelta=1') $l
$m = @(Mine $pc.id)
Check 'spento: copie non pubblicate cancellate dal server, la pubblicata resta' ($m.Count -eq 1 -and $m[0].name -eq 'Mia RDP' -and $m[0].state -eq 'published') ($m | ConvertTo-Json -Compress -Depth 4)

'--- permesso Scrittura'
[void](Call POST "/api/pcs/$($pc.id)/update" '{"permission":"write"}')
Set-Content "$appLog.go3" 'go'
$l = Wait-Log 'teams-personal: (p3b|errore)' 60
Check 'scrittura, interruttore riacceso: scritta in basso "condivide" subito' ($l -match 'permesso=write' -and $l -match "barra_vera='[^']*(condivide le connessioni salvate|shares saved)") $l
$l = Wait-Log 'teams-personal: (p4|errore)' 60
Check 'scrittura: Mia VNC nuova pubblicata subito e mostrata come Teams nello stesso giro' ($l -match 'Mia VNC\|teams' -and $l -notmatch 'Mia VNC\|locale') $l
Check 'scrittura: Mia SSH rifiutata prima resta locale anche a interruttore riacceso (ERRORI 112)' ($l -match 'Mia SSH\|locale' -and $l -notmatch 'Mia SSH\|teams') $l
Check 'sul server Mia SSH non condivisa, proposta ancora rifiutata con la nota' (-not (@((Call GET '/api/connections').Obj) | Where-Object { $_.name -eq 'Mia SSH' }) -and (@(Mine $pc.id) | Where-Object { $_.name -eq 'Mia SSH' }).state -eq 'rejected' -and (@(Mine $pc.id) | Where-Object { $_.name -eq 'Mia SSH' }).note -eq 'tienila tu')
Check 'sul server Mia VNC condivisa senza approvazione' (@((Call GET '/api/connections').Obj | Where-Object { $_.name -eq 'Mia VNC' -and $_.protocol -eq 'vnc' }).Count -eq 1)
$l = Wait-Log 'teams-personal: (p5 |errore)' 60
Check 'nuova voce: "Salva in" Teams di default' ($l -match 'salva_in_visibile=True scelta=1') $l
$l = Wait-Log 'teams-personal: (p5b|errore)' 60
Check 'interruttore spento: "Salva in" Locale di default' ($l -match 'interruttore_spento scelta=0') $l

'--- voce Teams eliminata: la locale ricompare'
[void](Call POST "/api/connections/$($shared.id)/delete" '{}')
Set-Content "$appLog.go4" 'go'
$l = Wait-Log 'teams-personal: (p6|errore)' 60
Check 'Mia RDP torna locale' ($l -match 'Mia RDP\|locale' -and $l -notmatch 'Mia RDP\|teams') $l

Start-Sleep 2
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
'--- righe del log dell''app'
Get-Content $appLog -Encoding UTF8
"=== PASS: $script:pass   FAIL: $script:fail"
