# Prova la regola della porta del server (dal pacchetto Windows):
# 1) server nuovo: usa la 25353; con la 25353 occupata prende la successiva
# 2) server configurato: la porta resta quella salvata e --port diverso viene rifiutato
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$exe = Join-Path $root '_release\server\win-x64\Lemora.Server.exe'
$fail = 0
function Check($name, $ok) { if ($ok) { "OK   $name" } else { "FAIL $name"; $script:fail++ } }
function Tmp { Join-Path ([IO.Path]::GetTempPath()) ('lemora-port-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
function Runtime($data) {
    for ($i = 0; $i -lt 40; $i++) {
        $f = Join-Path $data 'runtime.json'
        if (Test-Path $f) { try { return Get-Content $f -Raw | ConvertFrom-Json } catch { } }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
function Start-Srv($data, $extra) {
    $a = @('--foreground', '--no-pause', '--data', "`"$data`"") + $extra
    Start-Process $exe -ArgumentList $a -PassThru -WindowStyle Hidden -RedirectStandardOutput "$data.out.txt" -RedirectStandardError "$data.err.txt"
}

# 1a) porta predefinita libera
$d1 = Tmp
$p1 = Start-Srv $d1 @()
$rt = Runtime $d1
Check 'server nuovo usa la 25353' ($rt -and $rt.port -eq 25353)
# 1b) con la 25353 tenuta da questo, un secondo server nuovo prende la successiva
$d2 = Tmp
$p2 = Start-Srv $d2 @()
$rt2 = Runtime $d2
Check 'con la 25353 occupata prende la 25354' ($rt2 -and $rt2.port -eq 25354)
foreach ($p in $p1, $p2) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
Start-Sleep -Milliseconds 800

# 2) server configurato (server.json scritto a mano) sulla 25400
$d3 = Tmp
[IO.Directory]::CreateDirectory($d3) | Out-Null
$db = Join-Path $d3 'db'
[IO.Directory]::CreateDirectory($db) | Out-Null
'{"DbDir":"' + ($db -replace '\\', '\\') + '","Port":25400,"Listen":"all","Configured":true}' | Set-Content (Join-Path $d3 'server.json') -Encoding ascii
$p3 = Start-Srv $d3 @('--port', '25999')
$null = $p3.Handle # senza questo ExitCode resta vuoto
$p3.WaitForExit(15000) | Out-Null
Check 'configurato: --port diverso rifiutato (esce)' ($p3.HasExited -and $p3.ExitCode -eq 2)
Check 'messaggio chiaro' ((Get-Content "$d3.err.txt" -Raw) -match 'non e'' ammesso')
$p4 = Start-Srv $d3 @()
$rt4 = Runtime $d3
Check 'configurato: resta sulla porta salvata 25400' ($rt4 -and $rt4.port -eq 25400)
if ($p4 -and -not $p4.HasExited) { Stop-Process -Id $p4.Id -Force }
Start-Sleep -Milliseconds 800
# 3) installazione con --port: la scelta si salva in server.json (qui con l'opzione di prova, senza installare nulla)
$d5 = Tmp
$p5 = Start-Process $exe -ArgumentList '--save-port-test', '--port', '9100', '--data', "`"$d5`"" -PassThru -WindowStyle Hidden -Wait
$null = $p5.Handle
Check 'salva-porta: esce 0' ($p5.ExitCode -eq 0)
$j = Get-Content (Join-Path $d5 'server.json') -Raw | ConvertFrom-Json
Check 'salva-porta: server.json ha 9100 e non e'' configurato' ($j.Port -eq 9100 -and -not $j.Configured)
$p6 = Start-Srv $d5 @()
$rt6 = Runtime $d5
Check 'senza opzioni il server parte sulla 9100 salvata' ($rt6 -and $rt6.port -eq 9100)
if ($p6 -and -not $p6.HasExited) { Stop-Process -Id $p6.Id -Force }
Start-Sleep -Milliseconds 800
$p7 = Start-Process $exe -ArgumentList '--save-port-test', '--port', '25999', '--data', "`"$d3`"" -PassThru -WindowStyle Hidden -Wait
$null = $p7.Handle
Check 'salva-porta su server configurato con porta diversa: rifiutato' ($p7.ExitCode -eq 3)
foreach ($d in $d1, $d2, $d3, $d5) { if (Test-Path $d) { [IO.Directory]::Delete($d, $true) }; foreach ($s in '.out.txt', '.err.txt') { if (Test-Path "$d$s") { [IO.File]::Delete("$d$s") } } }
"Fallite: $fail"
exit $fail
