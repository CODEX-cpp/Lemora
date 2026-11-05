# Prova in background di una demo interna (LEMORA_DEMO) con il finto server VNC (127.0.0.1:5902) e log del server.
# Uso:  .\ui-map\tools\demo-run.ps1 -Demo shortcuts -Seconds 55
# L'app parte nascosta (LEMORA_HEADLESS) in un'istanza isolata: non tocca schermo, mouse o tastiera, ne' la produzione.
param([string]$Demo = 'shortcuts', [int]$Seconds = 55, [int]$ServerDelay = 0)
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$tmp = $env:TEMP
$srvLog = Join-Path $tmp "vnc_srv_$Demo.log"
$appLog = Join-Path $tmp "app_$Demo.log"
foreach ($f in $srvLog, $appLog) { if (Test-Path $f) { Remove-Item $f -Force } }
Get-NetTCPConnection -LocalPort 5902 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }
$srv = Start-Process python -ArgumentList '-u', "$root\ui-map\tools\fake-servers\vnc_multi.py", 'row3', "$ServerDelay" -RedirectStandardOutput $srvLog -RedirectStandardError "$tmp\vnc_srv.err" -WindowStyle Hidden -PassThru
Start-Sleep 2
$run = "$root\ui-map\_run\restyle"
$env:LEMORA_INSTANCE_ID = 'restyle'; $env:LEMORA_HEADLESS = '1'; $env:LEMORA_DEMO = $Demo; $env:LEMORA_NO_HOTKEY = '1'
$p = Start-Process "$run\Lemora.exe" -WorkingDirectory $run -RedirectStandardOutput $appLog -RedirectStandardError "$tmp\app_$Demo.err" -WindowStyle Hidden -PassThru
Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_DEMO, Env:LEMORA_NO_HOTKEY
$t = 0
while ($t -lt $Seconds -and -not $p.HasExited) { Start-Sleep 1; $t++ }
$exited = $p.HasExited
if (-not $exited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep 1
Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
"app uscita da sola: $exited (dopo ~$t s)"
"---- APP"
Get-Content $appLog | Where-Object { $_ -match "DEMO $Demo|DEMO shortcuts|ECCEZ|VNC\(|chiusura" } | ForEach-Object { $_ -replace '^\[T:\d+\]\[([\d:.]+)\]\s+\w+\s+\[[^\]]*\]\s*', '$1 ' }
"---- SERVER"
Get-Content $srvLog
