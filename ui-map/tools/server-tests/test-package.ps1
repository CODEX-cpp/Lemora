# Prova i pacchetti del server (_release\server): avvio in primo piano con dati temporanei, console raggiungibile, file di stato creati.
# Windows: exe vero. Linux: lo stesso pacchetto dentro WSL (se presente). Non installa servizi.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$pk = Join-Path $root '_release\server'
$fail = 0
function Check($name, $ok) { if ($ok) { "OK   $name" } else { "FAIL $name"; $script:fail++ } }

# Certificato autofirmato: accetta qualsiasi certificato (solo per questa prova, solo localhost)
Add-Type @"
using System.Net; using System.Security.Cryptography.X509Certificates;
public class TrustAll : ICertificatePolicy { public bool CheckValidationResult(ServicePoint s, X509Certificate c, WebRequest r, int p) { return true; } }
"@
[Net.ServicePointManager]::CertificatePolicy = New-Object TrustAll
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

function Probe($port) {
    for ($i = 0; $i -lt 30; $i++) {
        try { return Invoke-WebRequest -Uri "https://127.0.0.1:$port/" -UseBasicParsing -TimeoutSec 3 } catch { Start-Sleep -Milliseconds 700 }
    }
    return $null
}

# --- Windows ---
$data = Join-Path ([IO.Path]::GetTempPath()) ('lemora-pk-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$port = 47651
$exe = Join-Path $pk 'win-x64\Lemora.Server.exe'
Check 'win exe presente' (Test-Path $exe)
$p = Start-Process $exe -ArgumentList '--foreground', '--no-pause', '--port', $port, '--data', "`"$data`"" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$data.out.txt" -RedirectStandardError "$data.err.txt"
try {
    $r = Probe $port
    Check 'win console risponde' ($r -and $r.StatusCode -eq 200)
    Check 'win pagina e'' la console (HTML incorporato)' ($r -and $r.Content -match '<html')
    Check 'win server.pfx (certificato) creato' (Test-Path (Join-Path $data 'server.pfx'))
    Check 'win setup-code.txt creato' (Test-Path (Join-Path $data 'setup-code.txt'))
    Check 'win runtime.json creato' (Test-Path (Join-Path $data 'runtime.json'))
} finally {
    if ($fail) { 'esci:'; Get-Content "$data.out.txt", "$data.err.txt" -ErrorAction SilentlyContinue }
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } else { "processo gia' terminato, codice $($p.ExitCode)" }
    Start-Sleep -Milliseconds 500
    if (Test-Path $data) { [IO.Directory]::Delete($data, $true) }
}

# --- Linux (WSL) ---
$wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
if ($wsl) {
    $sh = Join-Path $PSScriptRoot 'test-package-linux.sh'
    $lin = (wsl.exe wslpath -a ($pk -replace '\\', '/')) | Select-Object -First 1
    $out = wsl.exe bash (wsl.exe wslpath -a ($sh -replace '\\', '/')) "$lin/linux-x64/Lemora.Server"
    $out
    Check 'linux console risponde (WSL)' (($out -join "`n") -match 'LINUX_OK')
} else { 'SALTATO linux: WSL assente' }

"Fallite: $fail"
exit $fail
