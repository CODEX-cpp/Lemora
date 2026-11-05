# Prova della console web (pagine PC e utenti / Nuovo abbinamento) con un Edge nascosto pilotato via DevTools, contro un server vero.
# Il PC "client" e' simulato con richieste curl. Foto nella cartella indicata.
# porte casuali a ogni giro: un server o un Edge rimasto da una prova interrotta non puo' rispondere al posto di quelli nuovi
param([string]$Out = (Join-Path $PSScriptRoot '..\..\_run\console-e2e'), [int]$Port = (Get-Random -Minimum 20000 -Maximum 30000), [int]$Debug = (Get-Random -Minimum 30001 -Maximum 40000))
$exe = 'C:\_dev\_projects\_repos\1Remote\Server\bin\Release\net10.0\Lemora.Server.exe'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$work = Join-Path $env:TEMP ('console-test-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data, $Out | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$base = "https://127.0.0.1:$Port"
$script:pass = 0; $script:fail = 0

function Call($method, $path, $json = $null, [string]$bearer = '', [bool]$cookies = $false) {
    $a = @('-k', '-s', '-X', $method, '-w', '\n%{http_code}', '-H', 'X-Lemora: 1')
    if ($cookies) { $a += @('-b', $jar, '-c', $jar) }
    if ($bearer) { $a += @('-H', "Authorization: Bearer $bearer") }
    if ($json -ne $null) { Set-Content -Path $body -Value $json -NoNewline -Encoding ascii; $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$body") }
    $a += "$base$path"
    $out = (& curl.exe @a) -join "`n"
    $i = $out.LastIndexOf("`n")
    $text = if ($i -ge 0) { $out.Substring(0, $i) } else { '' }
    $obj = $null; try { if ($text) { $obj = $text | ConvertFrom-Json } } catch { }
    [pscustomobject]@{ Code = [int]$out.Substring($i + 1); Obj = $obj; Text = $text }
}
function Check($label, $cond, $detail = '') { if ($cond) { $script:pass++; "PASS  $label" } else { $script:fail++; "FAIL  $label   $detail" } }

$proc = Start-Process $exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Call GET '/api/state').Code -eq 200) { break } }
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
[void](Call POST '/api/setup/verify' (@{ code = $setup } | ConvertTo-Json -Compress) -cookies $true)
[void](Call POST '/api/setup/complete' (@{ dbDir = $db; user = 'admin'; password = 'Password-di-prova-123'; port = $Port; address = 'all' } | ConvertTo-Json -Compress) -cookies $true)
Start-Sleep 1

$prof = Join-Path $work 'edge'
$ep = Start-Process $edge -ArgumentList @('--headless=new', "--remote-debugging-port=$Debug", "--user-data-dir=$prof", '--ignore-certificate-errors', '--window-size=1280,900', '--no-first-run', 'about:blank') -PassThru
Start-Sleep 4
$wsUrl = ((Invoke-RestMethod "http://127.0.0.1:$Debug/json") | Where-Object { $_.type -eq 'page' } | Select-Object -First 1).webSocketDebuggerUrl
$sock = New-Object System.Net.WebSockets.ClientWebSocket
$sock.ConnectAsync([Uri]$wsUrl, [Threading.CancellationToken]::None).Wait()
$script:cdp = 0; $script:events = New-Object System.Collections.ArrayList
function Recv {
    $buf = New-Object byte[] 1048576; $sb = New-Object Text.StringBuilder
    do { $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $buf); $r = $sock.ReceiveAsync($seg, [Threading.CancellationToken]::None).Result; [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count)) } while (-not $r.EndOfMessage)
    $sb.ToString() | ConvertFrom-Json
}
function Send($method, $params = @{}) {
    $script:cdp++
    $bytes = [Text.Encoding]::UTF8.GetBytes((@{ id = $script:cdp; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress))
    $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $bytes)
    $sock.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
    while ($true) { $r = Recv; if ($r.id -eq $script:cdp) { return $r }; if ($r.method) { [void]$script:events.Add($r) } }
}
function Js($expr) {
    $r = Send 'Runtime.evaluate' @{ expression = $expr; awaitPromise = $true; returnByValue = $true }
    if ($r.result.exceptionDetails) { 'ERRORE JS: ' + $r.result.exceptionDetails.exception.description } else { $r.result.result.value }
}
function Shot($name) { $r = Send 'Page.captureScreenshot' @{ format = 'png' }; [IO.File]::WriteAllBytes((Join-Path $Out "$name.png"), [Convert]::FromBase64String($r.result.data)) }
$helpers = @'
window.__wait = ms => new Promise(r => setTimeout(r, ms));
window.__set = (id, v) => { const e = document.getElementById(id); e.value = v; e.dispatchEvent(new Event('input', { bubbles: true })); };
window.__click = (txt, sel) => { const b = [...document.querySelectorAll(sel || 'button')].find(x => x.textContent.trim().startsWith(txt)); if (!b) throw new Error('manca: ' + txt); b.click(); };
window.__txt = () => document.body.innerText.replace(/\s+/g, ' ');
window.addEventListener('unhandledrejection', e => { window.__err = String((e.reason && e.reason.stack) || e.reason); });
window.addEventListener('error', e => { window.__err = String(e.message); });
'@
function Nav($url) { [void](Send 'Page.navigate' @{ url = $url }); Start-Sleep 2; [void](Js $helpers) }
function Act($js, $wait = 3000) {
    # su un PC carico le risposte tardano: nessuna attesa sotto i 3 secondi (100 = il passo ha gia' la sua attesa lunga dentro)
    if ($wait -gt 100) { $wait = [Math]::Max($wait, 3000) } Js "(async () => { $js; await window.__wait($wait); return window.__txt(); })()" }

[void](Send 'Runtime.enable'); [void](Send 'Log.enable'); [void](Send 'Page.enable')
[void](Send 'Emulation.setEmulatedMedia' @{ features = @(@{ name = 'prefers-color-scheme'; value = 'dark' }) })

'--- accesso e panoramica'
Nav "$base/"
$t = Act  "window.__set('pwd','Password-di-prova-123'); document.querySelector('form').requestSubmit()" 2500
Check 'dopo l''accesso si vede la panoramica con i due conteggi a 0' ($t -match 'Panoramica' -and $t -match 'PC collegati 0' -and $t -match 'Richieste in attesa 0') $t
Shot '01-panoramica'

'--- PC e utenti vuoto'
$t = Act  "window.__click('PC e utenti')"
Check 'pagina vuota con frase utile' ($t -match 'Nessun PC ancora') $t
Shot '02-pc-vuoto'

if ($env:E2E_DEBUG -eq 'nav') {
    # diagnosi: clic sul menu e stato interno della console
    $t = Act  "window.__click('Nuovo abbinamento')" 2500
    'stato: ' + (Js 'JSON.stringify({ page: S.page, screen: S.screen, code: S.code, err: window.__err || null })')
    'bottoni: ' + (Js "[...document.querySelectorAll('button')].map(b => b.textContent.trim()).join(' | ')")
    'testo: ' + $t
    $sock.Dispose(); Stop-Process -Id $ep.Id -Force -ErrorAction SilentlyContinue; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    exit
}
'--- Nuovo abbinamento'
$t = Act  "window.__click('Nuovo abbinamento')"
Check 'senza codice: invito a crearlo' ($t -match 'Nessun codice attivo' -and $t -match 'Crea il codice') $t
$t = Act  "window.__click('Crea il codice')"
$code = Js "(document.getElementById('pair-code')||{}).textContent"
Check 'codice di 6 caratteri mostrato come ABC-DEF' ($code -match '^[A-Z2-9]{3}-[A-Z2-9]{3}$') $code
Check 'conto alla rovescia e indirizzo del server' ($t -match 'Scade tra (29|30):' -and $t -match 'https://') $t
Shot '03-nuovo-abbinamento'
$raw = $code -replace '-', ''
$t2 = Act  'await window.__wait(2200)' 100
Check 'il conto alla rovescia scorre' ((Js "document.querySelector('.timer').textContent") -match 'Scade tra (29|30):')
[void](Send 'Page.reload'); Start-Sleep 3; [void](Js $helpers)
$t = Act  "window.__click('Nuovo abbinamento')"
$code2 = Js "(document.getElementById('pair-code')||{}).textContent"
Check 'dopo aver ricaricato la pagina il codice e'' ancora li''' ($code2 -eq $code) "$code2 / $code"

'--- un PC chiede di collegarsi'
$r = Call POST '/api/pair/request' (@{ code = $raw; pc = 'PC10'; user = 'User01'; appVersion = '1.0' } | ConvertTo-Json -Compress)
Check 'richiesta del PC accettata dal server' ($r.Code -eq 200) $r.Text
$id = $r.Obj.id; $tok = $r.Obj.token
$t = Act  'await window.__wait(6500)' 100
$t = Js 'window.__txt()'
Check 'compare da sola nel menu il numero delle richieste (badge 1)' ((Js "(document.querySelector('.badge')||{}).textContent") -eq '1')
$t = Act  "window.__click('PC e utenti')"
Check 'richiesta in attesa con PC - utente e indirizzo' ($t -match 'Richieste in attesa \(1\)' -and $t -match 'PC10 - User01' -and $t -match '127\.0\.0\.1') $t
Shot '04-richiesta-in-attesa'

'--- approvazione con permesso di scrittura'
$t = Act  "const s = document.querySelector('.card select.perm'); s.value = 'write'; s.dispatchEvent(new Event('change', { bubbles: true })); await window.__wait(300); document.querySelector('.card input[type=checkbox]').click()" 400
Check 'scelte tenute dopo il ridisegno' ((Js "document.querySelector('.card select.perm').value") -eq 'write' -and (Js "document.querySelector('.card input[type=checkbox]').checked") -eq $true)
if ($env:E2E_DEBUG -eq 'approve') {
    [void](Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}' -cookies $true)
    'prima del clic (server): ' + (Call GET '/api/pcs' -cookies $true).Text
    'prima del clic (pagina): ' + (Js 'JSON.stringify(S.pcs.map(p => [p.id.slice(0, 6), p.state]))')
    $t = Act  "window.__click('Approva')" 4000
    'dopo il clic (server): ' + (Call GET '/api/pcs' -cookies $true).Text
    'dopo il clic (pagina): ' + (Js 'JSON.stringify(S.pcs.map(p => [p.id.slice(0, 6), p.state]))')
    'errori della pagina: ' + (Js 'window.__err || null')
    'processi Edge aperti: ' + (@(Get-Process msedge -ErrorAction SilentlyContinue).Count)
    $sock.Dispose(); Stop-Process -Id $ep.Id -Force -ErrorAction SilentlyContinue; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    exit
}
$t = Act  "window.__click('Approva')" 4000
Check 'nella tabella: Approvato, il permesso e'' Scrittura' ($t -match 'Approvato' -and (Js "document.querySelector('.tbl select.perm').value") -eq 'write') $t
Check 'il badge e'' sparito' ((Js "document.querySelector('.badge') === null") -eq $true)
Shot '05-approvato'
$r = Call POST '/api/pair/status' (@{ id = $id; token = $tok } | ConvertTo-Json -Compress)
Check 'il PC riceve chiave, permesso scrittura e non copiare' ($r.Obj.state -eq 'approved' -and $r.Obj.permission -eq 'write' -and $r.Obj.noCopy -eq $true -and $r.Obj.key) $r.Text
$key = $r.Obj.key

'--- cambio permesso'
$t = Act  "const s = document.querySelector('.tbl select.perm'); s.value = 'read'; s.dispatchEvent(new Event('change', { bubbles: true }))" 500
Check 'compare il pulsante Salva' ($t -match 'Salva')
$t = Act  "window.__click('Salva')" 1500
$r = Call GET '/api/client/me' -bearer $key
Check 'il PC vede il nuovo permesso (sola lettura)' ($r.Obj.permission -eq 'read') $r.Text

'--- scollega'
$t = Act  "window.__click('Scollega', '.tbl button')" 600
Check 'dialogo di conferma con il nome del PC' ($t -match 'Scollegare questo PC' -and $t -match 'PC10 - User01') $t
Shot '06-dialogo-scollega'
$t = Act  "window.__click('Annulla', '.dialog button')" 600
Check 'Annulla chiude il dialogo e non scollega' ((Js "document.querySelector('.dialog') === null") -eq $true -and (Call GET '/api/client/me' -bearer $key).Code -eq 200)
$t = Act  "window.__click('Scollega', '.tbl button'); await window.__wait(400); window.__click('Scollega', '.dialog button')" 1800
Check 'stato Scollegato' ($t -match 'Scollegato') $t
$r = Call GET '/api/client/me' -bearer $key
Check 'la chiave non vale piu'' (401, revoked)' ($r.Code -eq 401 -and $r.Obj.state -eq 'revoked') $r.Text
Shot '07-scollegato'

'--- rifiuto'
$t = Act  "window.__click('Nuovo abbinamento')"
$t = Act  "window.__click('Crea')" 800
$c3 = (Js "document.getElementById('pair-code').textContent") -replace '-', ''
$r = Call POST '/api/pair/request' (@{ code = $c3; pc = 'PC11'; user = 'User02' } | ConvertTo-Json -Compress)
$id2 = $r.Obj.id; $tok2 = $r.Obj.token
$t = Act  "window.__click('PC e utenti'); await window.__wait(6500)" 100
Check 'seconda richiesta visibile' ((Js 'window.__txt()') -match 'PC11 - User02')
$t = Act  "window.__click('Rifiuta')" 1800
Check 'stato Rifiutato nella tabella' ($t -match 'Rifiutato') $t
$r = Call POST '/api/pair/status' (@{ id = $id2; token = $tok2 } | ConvertTo-Json -Compress)
Check 'il PC vede Rifiutato' ($r.Obj.state -eq 'rejected')

'--- connessioni condivise'
[void](Call POST '/api/login' '{"user":"admin","password":"Password-di-prova-123"}' -cookies $true) # sessione curl per controllare i dati dal lato server
$t = Act  "window.__click('Connessioni condivise')" 1500
Check 'pagina vuota con invito a creare la prima connessione' ($t -match 'Nuova connessione' -and (@((Call GET '/api/connections' -cookies $true).Obj).Count -eq 0)) $t
Shot '11-connessioni-vuoto'
$t = Act  "window.__click('Nuova connessione')" 700
Check 'si apre il form con focus sul nome' ((Js "document.activeElement && document.activeElement.id") -eq 'cf-name')
Shot '12-form-connessione'
Check 'il form non ha scorrimento orizzontale (il campo Porta resta nel riquadro)' ((Js "(() => { const d = document.querySelector('.dialog'); return d.scrollWidth <= d.clientWidth })()") -eq $true)
if ($env:E2E_DEBUG -eq 'form') {
    [void](Send 'Emulation.setDeviceMetricsOverride' @{ width = 390; height = 800; deviceScaleFactor = 1; mobile = $true })
    Start-Sleep 1
    Shot '12b-form-telefono'
    'form a 390 px senza scorrimento: ' + (Js "(() => { const d = document.querySelector('.dialog'); return d.scrollWidth <= d.clientWidth })()")
    $sock.Dispose(); Stop-Process -Id $ep.Id -Force -ErrorAction SilentlyContinue; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    exit
}
$t = Act  "document.querySelector('.dialog form').requestSubmit()" 1500
Check 'nome vuoto: errore sul campo e nulla creato' ((Js "document.getElementById('cf-name').closest('.field').classList.contains('err')") -eq $true -and (@((Call GET '/api/connections' -cookies $true).Obj).Count -eq 0))
$t = Act  "window.__set('cf-protocol','ssh')" 300
Check 'scegliendo SSH la porta passa a 22' ((Js "document.getElementById('cf-port').value") -eq '22')
$t = Act  "window.__set('cf-name','Server contabilita'); window.__set('cf-folder','Uffici/Sede'); window.__set('cf-host','srv.lan'); window.__set('cf-user','root'); window.__set('cf-password','Segreta-123#'); window.__set('cf-tags','linux, prod')" 300
if ($env:E2E_DEBUG -eq 'conn') {
    'form: ' + (Js 'JSON.stringify(S.connForm)')
    'invio diretto: ' + (Js "(async () => { const F = S.connForm; const body = { ...F.v, name: F.v.name.trim(), port: parseInt(F.v.port, 10) }; const r = await fetch('/api/connections', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-Lemora': '1' }, body: JSON.stringify(body) }); return r.status + ' ' + (await r.text()) + ' ' + JSON.stringify(body); })()")
    'campi nel DOM: ' + (Js "['name','folder','protocol','host','port','user','password','tags'].map(k => k + '=' + (document.getElementById('cf-' + k) || {}).value).join(' | ')")
    $sock.Dispose(); Stop-Process -Id $ep.Id -Force -ErrorAction SilentlyContinue; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    exit
}
$t = Act  "document.querySelector('.dialog form').requestSubmit()" 4000
Check 'la connessione compare in tabella con cartella, indirizzo e protocollo' ($t -match 'Server contabilita' -and $t -match 'Uffici/Sede' -and $t -match 'srv\.lan:22' -and $t -match 'SSH') $t
$list = @((Call GET '/api/connections' -cookies $true).Obj)
Check 'sul server: una voce, con password, tag normalizzati, senza password in chiaro nell''elenco' ($list.Count -eq 1 -and $list[0].hasPassword -eq $true -and $list[0].tags -eq 'linux,prod' -and ((Call GET '/api/connections' -cookies $true).Text -notmatch 'Segreta'))
$cid = $list[0].id
Shot '13-connessione-creata'
$t = Act  "const i = document.querySelector('.toolbar input'); i.value = 'zzz'; i.dispatchEvent(new Event('input', { bubbles: true }))" 600
Check 'la ricerca senza risultati lo dice' (-not ($t -match 'srv\.lan'))
$t = Act  "const i = document.querySelector('.toolbar input'); i.value = 'PROD'; i.dispatchEvent(new Event('input', { bubbles: true }))" 600
Check 'la ricerca trova anche per tag, senza badare alle maiuscole' ($t -match 'srv\.lan')
$t = Act  "const i = document.querySelector('.toolbar input'); i.value = ''; i.dispatchEvent(new Event('input', { bubbles: true }))" 400
$t = Act  "window.__click('Password', 'table button')" 700
Check 'mostra password: prima chiede conferma e avverte che viene registrato' ($t -match 'registr')
Shot '14-conferma-password'
$t = Act  "document.querySelector('.dialog button.primary').click()" 1200
Check 'poi mostra la password giusta' ((Js "(document.querySelector('.revealed')||{}).textContent") -eq 'Segreta-123#')
$t = Act  "document.querySelector('.dialog button.primary').click()" 600
Check 'chiuso il dialogo la password non resta nella pagina' ((Js "document.body.innerText.indexOf('Segreta-123#') < 0") -eq $true)
$t = Act  "window.__click('Modifica', 'table button')" 700
Check 'modifica: i campi sono riempiti e la password e'' vuota' ((Js "document.getElementById('cf-name').value") -eq 'Server contabilita' -and (Js "document.getElementById('cf-password').value") -eq '')
$t = Act  "window.__set('cf-name','Server contabilita 2')" 200
$t = Act  "document.querySelector('.dialog form').requestSubmit()" 4000
$r = Call POST "/api/connections/$cid/reveal" '{}' -cookies $true
Check 'modifica salvata, password invariata, versione 2' ($t -match 'Server contabilita 2' -and $r.Obj.password -eq 'Segreta-123#' -and (@((Call GET '/api/connections' -cookies $true).Obj)[0].version -eq 2)) $t
$t = Act  "window.__click('Disabilita', 'table button')" 1500
Check 'disabilita: stato Disabilitata' ($t -match 'Disabilitata' -and (@((Call GET '/api/connections' -cookies $true).Obj)[0].disabled -eq $true)) $t
Shot '15-disabilitata'
$t = Act  "window.__click('Abilita', 'table button')" 1500
Check 'riabilita: torna Attiva' ($t -match 'Attiva' -and (@((Call GET '/api/connections' -cookies $true).Obj)[0].disabled -eq $false)) $t
$t = Act  "window.__click('Panoramica')" 800
Check 'in Panoramica il riquadro delle connessioni dice 1' ($t -match 'Connessioni condivise 1') $t
$t = Act  "window.__click('Connessioni condivise')" 800
$t = Act  "window.__click('Elimina', 'table button')" 700
Check 'elimina: dialogo di conferma col nome' ($t -match 'Server contabilita 2')
Shot '16-conferma-elimina'
$t = Act  "document.querySelector('.dialog button.danger').click()" 1800
Check 'eliminata: la tabella torna vuota e sul server non c''e'' piu''' ($t -match 'Nuova connessione' -and (@((Call GET '/api/connections' -cookies $true).Obj).Count -eq 0))
$t = Act  "window.__click('Nuova connessione')" 500
$t = Act  "(document.activeElement || document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))" 500 # come dalla tastiera: l'evento parte dall'elemento col focus
Check 'Esc chiude il form' ((Js "document.querySelector('.dialog') === null") -eq $true)
Shot '17-dopo-elimina'

'--- lingua, tema chiaro, telefono'
$t = Act  "window.__click('PC e utenti')" 800
$t = Act  "window.__click('EN', '.seg button')" 800
Check 'in inglese: menu e tabella tradotti' ($t -match 'PCs and users' -and $t -match 'Disconnected' -and $t -match 'Rejected') $t
Shot '08-inglese'
[void](Send 'Emulation.setEmulatedMedia' @{ features = @(@{ name = 'prefers-color-scheme'; value = 'light' }) })
$t = Act  "window.__click('IT', '.seg button')" 800
Shot '09-chiaro'
[void](Send 'Emulation.setDeviceMetricsOverride' @{ width = 390; height = 800; deviceScaleFactor = 1; mobile = $true })
Start-Sleep 1
Shot '10-telefono'
$overflow = Js 'document.documentElement.scrollWidth > document.documentElement.clientWidth'
Check 'a 390 px non c''e'' scorrimento orizzontale della pagina' ($overflow -eq $false) "scrollWidth>clientWidth: $overflow"
[void](Send 'Emulation.clearDeviceMetricsOverride')

'--- sessione scaduta'
[void](Call POST '/api/logout' '{}' -cookies $false)
$t = Act  "document.querySelector('.chip-btn').click()" 1200
Check 'Esci riporta all''accesso' ($t -match 'Accedi')

'--- messaggi della console del browser'
$bad = $script:events | Where-Object { -not ($_.method -eq 'Log.entryAdded' -and $_.params.entry.url -match '/api/connections$' -and $_.params.entry.text -match 'status of 400') } | # il 400 e' l'invio con nome vuoto, provocato apposta
    Where-Object { $_.method -eq 'Runtime.exceptionThrown' -or ($_.method -eq 'Log.entryAdded' -and $_.params.entry.level -in 'error', 'warning') -or ($_.method -eq 'Runtime.consoleAPICalled' -and $_.params.type -in 'error', 'warning') }
Check 'nessun errore o avviso nella console del browser' (-not $bad) (($bad | ForEach-Object { $_ | ConvertTo-Json -Depth 6 -Compress }) -join "`n")

$sock.Dispose()
Stop-Process -Id $ep.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 2
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
"=== PASS: $script:pass   FAIL: $script:fail"
