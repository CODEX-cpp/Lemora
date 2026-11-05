# Foto della colonna "Sincronizzazione" in "PC e utenti" della console (tema scuro e chiaro), con un Edge nascosto contro un server vero su porta casuale.
# Quattro PC: sincronizzazione attiva, disattivata dal PC, non ancora nota, bloccata dall'amministratore. Foto in ui-map/_shots_tmp/console-sync-*.png
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

function NewPc($name, $perm) {
    $c = (Call POST '/api/pairing/new' '{}' -cookies $true).Obj.code
    $r = Call POST '/api/pair/request' (J @{ code = $c; pc = $name; user = 'Ivan' })
    [void](Call POST "/api/pcs/$($r.Obj.id)/approve" (J @{ permission = $perm }) -cookies $true)
    $key = (Call POST '/api/pair/status' (J @{ id = $r.Obj.id; token = $r.Obj.token })).Obj.key
    [pscustomobject]@{ Id = $r.Obj.id; Key = $key }
}
function Share($pc, [bool]$on) { [void](Call POST '/api/client/personal' (J @{ share = $on; items = @() }) -bearer $pc.Key -caps 'personal') }
$a = NewPc 'PC-ATTIVO' 'write'; Share $a $true
$b = NewPc 'PC-DISATTIVATO' 'write'; Share $b $true; Start-Sleep 1; Share $b $false
$c = NewPc 'PC-NUOVO' 'write'
$d = NewPc 'PC-BLOCCATO' 'write'; Share $d $true
[void](Call POST "/api/pcs/$($d.Id)/update" (J @{ noCopy = $true }) -cookies $true)

$prof = Join-Path $work 'edge'
$ep = Start-Process $edge -ArgumentList @('--headless=new', "--remote-debugging-port=$Debug", "--user-data-dir=$prof", '--ignore-certificate-errors', '--window-size=1280,700', '--no-first-run', 'about:blank') -PassThru
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
$t = Act "window.__click('PC e utenti', '.nav button')" 1500
Check 'colonna Sincronizzazione con i quattro stati' ($t -match 'Sincronizzazione' -and $t -match 'Attiva' -and $t -match 'Disattivata dal PC' -and $t -match 'Non ancora nota' -and $t -match 'Bloccata dall.amministratore') $t
foreach ($scheme in 'dark', 'light') {
    [void](Send 'Emulation.setEmulatedMedia' @{ features = @(@{ name = 'prefers-color-scheme'; value = $scheme }) })
    Start-Sleep 1
    Shot "console-sync-$scheme"
}
$ov = Js "(() => { const t = document.querySelector('table'); return t ? t.scrollWidth + '>' + t.parentElement.clientWidth : 'no-table'; })()"
"larghezza tabella/contenitore: $ov"

$sock.Dispose(); Stop-Process -Id $ep.Id -Force -ErrorAction SilentlyContinue; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1
try { [IO.Directory]::Delete($work, $true) } catch { }
"=== PASS: $script:pass   FAIL: $script:fail   (foto in $Out)"
