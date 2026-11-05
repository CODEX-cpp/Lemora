# Prova veloce della sola pagina "Credenziali e cartelle" della console, con un Edge nascosto (profilo usa e getta) pilotato via DevTools, contro un server vero
# su porta casuale e dati temporanei. Foto della pagina in ui-map/_shots_tmp/console-credenziali.png (e una per ogni scheda).
param([string]$Out = (Join-Path $PSScriptRoot '..\..\_shots_tmp'), [int]$Port = (Get-Random -Minimum 20000 -Maximum 30000), [int]$Debug = (Get-Random -Minimum 30001 -Maximum 40000))
$exe = 'C:\_dev\_projects\_repos\1Remote\Server\bin\Release\net10.0\Lemora.Server.exe'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$work = Join-Path $env:TEMP ('console-test-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $work 'data'; $db = Join-Path $work 'db'
New-Item -ItemType Directory -Force $data, $Out | Out-Null
$jar = Join-Path $work 'cookies.txt'; $body = Join-Path $work 'body.json'
$base = "https://127.0.0.1:$Port"
$script:pass = 0; $script:fail = 0

function Call($method, $path, $json = $null, [string]$bearer = '', [bool]$cookies = $false, [string]$caps = '') {
    $a = @('-k', '-s', '-X', $method, '-w', '\n%{http_code}', '-H', 'X-Lemora: 1')
    if ($cookies) { $a += @('-b', $jar, '-c', $jar) }
    if ($bearer) { $a += @('-H', "Authorization: Bearer $bearer") }
    if ($caps) { $a += @('-H', "X-Lemora-Caps: $caps") }
    if ($json -ne $null) { [IO.File]::WriteAllText($body, $json, [Text.UTF8Encoding]::new($false)); $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$body") }
    $a += "$base$path"
    $o = (& curl.exe @a) -join "`n"
    $i = $o.LastIndexOf("`n")
    $text = if ($i -ge 0) { $o.Substring(0, $i) } else { '' }
    $obj = $null; try { if ($text) { $obj = $text | ConvertFrom-Json } } catch { }
    [pscustomobject]@{ Code = [int]$o.Substring($i + 1); Obj = $obj; Text = $text }
}
function J($o) { $o | ConvertTo-Json -Compress -Depth 6 }
function Check($label, $cond, $detail = '') { if ($cond) { $script:pass++; "PASS  $label" } else { $script:fail++; "FAIL  $label   $detail" } }

$proc = Start-Process $exe -ArgumentList @('--foreground', '--data', "`"$data`"", '--port', $Port) -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ((Call GET '/api/state').Code -eq 200) { break } }
$setup = (Get-Content (Join-Path $data 'setup-code.txt')).Trim()
[void](Call POST '/api/setup/verify' (J @{ code = $setup }) -cookies $true)
[void](Call POST '/api/setup/complete' (J @{ dbDir = $db; user = 'admin'; password = 'Password-di-prova-123'; port = $Port; address = 'all' }) -cookies $true)
Start-Sleep 1
[void](Call POST '/api/login' (J @{ user = 'admin'; password = 'Password-di-prova-123' }) -cookies $true)

# dati di prova creati da un PC con scrittura
$c = (Call POST '/api/pairing/new' '{}' -cookies $true).Obj.code
$r = Call POST '/api/pair/request' (J @{ code = $c; pc = 'PC-PROVA'; user = 'Ivan' })
[void](Call POST "/api/pcs/$($r.Obj.id)/approve" (J @{ permission = 'write' }) -cookies $true)
$key = (Call POST '/api/pair/status' (J @{ id = $r.Obj.id; token = $r.Obj.token })).Obj.key
function Pc($path, $o) { Call POST $path (J $o) -bearer $key -caps 'shared' }
[void](Call POST '/api/folders' (J @{ path = 'Clienti/Rossi'; domain = 'rossi.local'; ipPrefix = '10.20.' }) -cookies $true)
[void](Call POST '/api/folders' (J @{ path = 'Clienti/Bianchi'; domain = 'bianchi.lan'; ipPrefix = '192.168.5.' }) -cookies $true)
[void](Call POST '/api/folders' (J @{ path = 'Uffici' }) -cookies $true)
[void](Call POST '/api/connections' (J @{ folder = 'Clienti/Rossi'; name = 'Server Rossi'; protocol = 'rdp'; host = '10.20.0.5'; port = 3389; user = 'admin'; password = ''; tags = '' }) -cookies $true)
[void](Pc '/api/client/devices' @{ folder = 'Clienti/Rossi'; name = 'Router sede'; ip = '10.20.0.1'; users = @(@{ user = 'admin'; password = 'SegretoDisp-9!z' }, @{ user = 'ospite'; password = '' }) })
[void](Pc '/api/client/devices' @{ folder = 'Clienti/Bianchi'; name = 'Switch piano 1'; ip = '192.168.5.2'; users = @(@{ user = 'root'; password = 'SegretoDisp-8?y' }) })
[void](Pc '/api/client/fcreds' @{ folder = 'Clienti/Rossi'; user = 'ROSSI\amministratore'; password = 'SegretoCred-7*x' })
[void](Pc '/api/client/fcreds' @{ folder = 'Clienti/Bianchi'; user = 'svc-backup'; password = 'SegretoCred-6&w' })

$prof = Join-Path $work 'edge'
$ep = Start-Process $edge -ArgumentList @('--headless=new', "--remote-debugging-port=$Debug", "--user-data-dir=$prof", '--ignore-certificate-errors', '--window-size=1280,900', '--no-first-run', 'about:blank') -PassThru
Start-Sleep 4
$wsUrl = ((Invoke-RestMethod "http://127.0.0.1:$Debug/json") | Where-Object { $_.type -eq 'page' } | Select-Object -First 1).webSocketDebuggerUrl
$sock = New-Object System.Net.WebSockets.ClientWebSocket
$sock.ConnectAsync([Uri]$wsUrl, [Threading.CancellationToken]::None).Wait()
$script:cdp = 0
function Recv {
    $buf = New-Object byte[] 4194304; $sb = New-Object Text.StringBuilder
    do { $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $buf); $x = $sock.ReceiveAsync($seg, [Threading.CancellationToken]::None).Result; [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $x.Count)) } while (-not $x.EndOfMessage)
    $sb.ToString() | ConvertFrom-Json
}
function Send($method, $params = @{}) {
    $script:cdp++
    $bytes = [Text.Encoding]::UTF8.GetBytes((@{ id = $script:cdp; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress))
    $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $bytes)
    $sock.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
    while ($true) { $x = Recv; if ($x.id -eq $script:cdp) { return $x } }
}
function Js($expr) {
    $x = Send 'Runtime.evaluate' @{ expression = $expr; awaitPromise = $true; returnByValue = $true }
    if ($x.result.exceptionDetails) { 'ERRORE JS: ' + $x.result.exceptionDetails.exception.description } else { $x.result.result.value }
}
function Shot($name) { $x = Send 'Page.captureScreenshot' @{ format = 'png' }; [IO.File]::WriteAllBytes((Join-Path $Out "$name.png"), [Convert]::FromBase64String($x.result.data)) }
$helpers = @'
window.__wait = ms => new Promise(r => setTimeout(r, ms));
window.__set = (id, v) => { const e = document.getElementById(id); e.value = v; e.dispatchEvent(new Event('input', { bubbles: true })); };
window.__click = (txt, sel) => { const b = [...document.querySelectorAll(sel || 'button')].find(x => x.textContent.trim().startsWith(txt)); if (!b) throw new Error('manca: ' + txt); b.click(); };
window.__txt = () => document.body.innerText.replace(/\s+/g, ' ');
'@
function Act($js, $wait = 900) { Js "(async () => { $js; await window.__wait($wait); return window.__txt(); })()" }

[void](Send 'Runtime.enable'); [void](Send 'Page.enable')
[void](Send 'Page.navigate' @{ url = "$base/" }); Start-Sleep 2; [void](Js $helpers)
$t = Act "window.__set('pwd','Password-di-prova-123'); document.querySelector('form').requestSubmit()" 1500
Check 'accesso alla console' ($t -match 'Panoramica') $t

'--- pagina Credenziali e cartelle'
$t = Act "window.__click('Credenziali e cartelle', '.nav button')" 1500
Check 'voce di menu e titolo' ($t -match 'Credenziali e cartelle' -and $t -match 'Cartelle' -and $t -match 'Password dei dispositivi') $t
Check 'tab Cartelle: percorso, dominio, prefisso IP e conteggi' ($t -match 'Clienti/Rossi' -and $t -match 'rossi\.local' -and $t -match '10\.20\.' -and $t -match '1 connessioni . 1 dispositivi . 1 credenziali') $t
Shot 'console-credenziali'
$t = Act "window.__click('Credenziali di cartella')"
Check 'tab Credenziali di cartella: cartella e utente, nessuna password' ($t -match 'ROSSI\\amministratore' -and $t -match 'svc-backup' -and $t -notmatch 'Segreto') $t
Shot 'console-credenziali-2-credenziali'
$t = Act "window.__click('Password dei dispositivi')"
Check 'tab Password dei dispositivi: nome, IP, utenti, numero utenti, nessuna password' ($t -match 'Router sede' -and $t -match '10\.20\.0\.1' -and $t -match '2 utenti' -and $t -match 'admin, ospite' -and $t -notmatch 'Segreto') $t
Shot 'console-credenziali-3-dispositivi'
$t = Act "window.__set('sh-q', 'switch')"
Check 'ricerca: resta solo lo Switch' ($t -match 'Switch piano 1' -and $t -notmatch 'Router sede') $t
$t = Act "window.__set('sh-q', 'zzz')"
Check 'ricerca senza risultati lo dice' ($t -match 'Niente corrisponde') $t
$t = Act "window.__set('sh-q', ''); window.__click('Cartelle')"

'--- nuova cartella'
$t = Act "window.__click('Nuova cartella')"
Check 'dialogo con percorso, dominio e prefisso' ($t -match 'Percorso' -and $t -match 'Dominio' -and $t -match 'Prefisso IP') $t
$t = Act "window.__set('sh-path', 'clienti/rossi'); document.querySelector('.dialog form').requestSubmit()"
# i caratteri accentati nei criteri sono "." : PowerShell 5.1 legge questo file come ANSI e li rovinerebbe
Check 'cartella gia esistente: messaggio chiaro e dialogo ancora aperto' ($t -match 'Esiste gi. una cartella' -and $t -match 'Annulla Salva') $t
$t = Act "window.__set('sh-path', 'Nuovi/Clienti'); window.__set('sh-domain', 'nuovi.lan'); window.__set('sh-ipPrefix', '172.16.'); document.querySelector('.dialog form').requestSubmit()" 1500
Check 'la cartella nuova compare in tabella' ($t -match 'Nuovi/Clienti' -and $t -match 'nuovi\.lan' -and $t -match '172\.16\.') $t
Check 'sul server: la cartella esiste con le impostazioni' (((Call GET '/api/folders' -cookies $true).Obj | Where-Object { $_.path -eq 'Nuovi/Clienti' }).ipPrefix -eq '172.16.')

'--- modifica e rinomina'
$t = Act "[...document.querySelectorAll('button')].find(b => b.getAttribute('aria-label') === 'Modifica: Nuovi/Clienti').click()"
Check 'modifica: il percorso e'' fisso, dominio e prefisso modificabili' ($t -match 'Modifica cartella' -and $t -match 'Nuovi/Clienti') $t
$t = Act "window.__set('sh-domain', 'altro.lan'); document.querySelector('.dialog form').requestSubmit()" 1500
Check 'dominio cambiato' ($t -match 'altro\.lan') $t
$t = Act "[...document.querySelectorAll('button')].find(b => b.getAttribute('aria-label') === 'Rinomina: Nuovi/Clienti').click()"
Check 'dialogo rinomina' ($t -match 'Rinomina cartella' -and $t -match 'Nuovo percorso') $t
$t = Act "window.__set('sh-to', 'Uffici'); document.querySelector('.dialog form').requestSubmit()"
Check 'rinomina verso cartella esistente: messaggio' ($t -match 'Esiste gi. una cartella') $t
$t = Act "window.__set('sh-to', 'Nuovi/Clienti/Dentro'); document.querySelector('.dialog form').requestSubmit()"
Check 'rinomina dentro se stessa: messaggio' ($t -match 'non si pu. spostare dentro se stessa') $t
$t = Act "window.__set('sh-to', 'Rinominata/Clienti'); document.querySelector('.dialog form').requestSubmit()" 1500
Check 'rinomina riuscita' ($t -match 'Rinominata/Clienti' -and $t -notmatch 'Nuovi/Clienti') $t

'--- eliminare'
$t = Act "[...document.querySelectorAll('button')].find(b => b.getAttribute('aria-label') === 'Elimina: Clienti/Rossi').click()"
Check 'dialogo di conferma per la cartella' ($t -match 'Eliminare la cartella' -and $t -match 'Clienti/Rossi') $t
Shot 'console-credenziali-4-conferma'
$t = Act "window.__click('Elimina', '.dialog button.danger')" 1500
Check 'cartella con connessione condivisa: rifiutata con messaggio chiaro, la cartella resta' ($t -match 'contiene connessioni condivise' -and $t -match 'Clienti/Rossi') $t
$t = Act "[...document.querySelectorAll('button')].find(b => b.getAttribute('aria-label') === 'Elimina: Rinominata/Clienti').click(); await window.__wait(300); window.__click('Elimina', '.dialog button.danger')" 1500
Check 'cartella vuota eliminata' ($t -notmatch 'Rinominata/Clienti') $t
$t = Act "window.__click('Credenziali di cartella')"
$t = Act "[...document.querySelectorAll('button')].find(b => b.getAttribute('aria-label') === 'Elimina: svc-backup').click()"
Check 'conferma per la credenziale' ($t -match 'Eliminare la credenziale' -and $t -match 'svc-backup') $t
$t = Act "window.__click('Annulla', '.dialog button')"
Check 'Annulla non elimina' ($t -match 'svc-backup') $t
$t = Act "[...document.querySelectorAll('button')].find(b => b.getAttribute('aria-label') === 'Elimina: svc-backup').click(); await window.__wait(300); window.__click('Elimina', '.dialog button.danger')" 1500
Check 'credenziale eliminata' ($t -notmatch 'svc-backup' -and $t -match 'ROSSI') $t
$t = Act "window.__click('Password dei dispositivi')"
$t = Act "[...document.querySelectorAll('button')].find(b => b.getAttribute('aria-label') === 'Elimina: Switch piano 1').click(); await window.__wait(300); window.__click('Elimina', '.dialog button.danger')" 1500
Check 'dispositivo eliminato' ($t -notmatch 'Switch piano 1' -and $t -match 'Router sede') $t
$t = Act "window.__click('Registro attivit', '.nav button')" 1500
Check 'registro attivita: eventi dell''amministratore con etichette leggibili' ($t -match 'Cartella creata' -and $t -match 'Cartella rinominata' -and $t -match 'Cartella eliminata' -and $t -match 'Dispositivo eliminato' -and $t -match 'Credenziale di cartella eliminata') $t
Shot 'console-credenziali-5-registro'
Check 'nessuna password di prova nella pagina' ((Js 'document.body.innerText') -notmatch 'Segreto')

$sock.Dispose(); Stop-Process -Id $ep.Id -Force -ErrorAction SilentlyContinue; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
"=== PASS: $script:pass   FAIL: $script:fail   (foto in $Out)"
