# Test of the launcher (Lemora.exe built from Installer\launcher) in a temporary folder, without touching any real installation.
# Fake "app" = cmd.exe copied as versions\<v>\Lemora.exe: it writes a file in its folder, showing with which arguments and from where it started.
param([string]$Launcher = (Join-Path $PSScriptRoot '..\..\ui-map\_build_launcher\release\Lemora.exe'))
$script:pass = 0; $script:fail = 0
function Check($label, $cond, $detail = '') { if ($cond) { $script:pass++; "PASS  $label" } else { $script:fail++; "FAIL  $label   $detail" } }
function Wait-File($p, $sec = 8) { for ($i = 0; $i -lt $sec * 10; $i++) { if (Test-Path $p) { return $true }; Start-Sleep -Milliseconds 100 }; $false }
$cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'

function New-Install($name, $versions, $current) {
    $d = Join-Path $env:TEMP ("lemora-launcher-test-$name-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $d | Out-Null
    Copy-Item $Launcher (Join-Path $d 'Lemora.exe')
    foreach ($v in $versions) { $vd = Join-Path $d "versions\$v"; New-Item -ItemType Directory -Force $vd | Out-Null; Copy-Item $cmd (Join-Path $vd 'Lemora.exe') }
    if ($current -ne $null) { [IO.File]::WriteAllText((Join-Path $d 'current.txt'), $current) }
    $d
}
function Run($d, $argLine) {
    $sp = @{ FilePath = (Join-Path $d 'Lemora.exe'); WorkingDirectory = $d; WindowStyle = 'Hidden'; PassThru = $true }
    if ($argLine) { $sp.ArgumentList = $argLine }
    Start-Process @sp
}
function Clean($d) { try { [IO.Directory]::Delete($d, $true) } catch { } }

'--- normal start: the version named in current.txt starts, with the arguments and from its folder'
$d = New-Install 'ok' @('1.0.0.1', '1.0.0.2') '1.0.0.1'
Run $d '/c echo %CD% > out.txt & echo ARG >> out.txt' | Out-Null
$out = Join-Path $d 'versions\1.0.0.1\out.txt'
Check 'the version in current.txt starts (1.0.0.1)' (Wait-File $out) 'out.txt did not appear'
Check 'the working folder is the version folder' ((Get-Content $out -ErrorAction SilentlyContinue) -match '1\.0\.0\.1') ''
Check 'the other version did not start' (-not (Test-Path (Join-Path $d 'versions\1.0.0.2\out.txt')))
Clean $d

'--- current.txt changed: the new version starts'
$d = New-Install 'change' @('1.0.0.1', '1.0.0.2') '1.0.0.1'
[IO.File]::WriteAllText((Join-Path $d 'current.txt'), '1.0.0.2')
Run $d '/c echo yes > out.txt' | Out-Null
Check '1.0.0.2 starts' (Wait-File (Join-Path $d 'versions\1.0.0.2\out.txt'))
Clean $d

'--- wrong current.txt: the most recent version starts and it is written to the error file'
$d = New-Install 'broken' @('1.0.0.9', '1.0.0.10') 'does-not-exist'
Run $d '/c echo yes > out.txt' | Out-Null
Check 'the most recent starts (1.0.0.10, numeric and not text comparison)' (Wait-File (Join-Path $d 'versions\1.0.0.10\out.txt'))
Check 'launcher-error.log explains what happened' ((Get-Content (Join-Path $d 'launcher-error.log') -ErrorAction SilentlyContinue) -match 'does-not-exist') ''
Clean $d

'--- no version: no crash, error written'
$d = New-Install 'empty' @() $null
$p = Run $d ''
$p.WaitForExit(5000) | Out-Null
Check 'the launcher exits by itself' $p.HasExited
# the message follows the Windows UI language (Italian or English)
Check 'error written' ((Get-Content (Join-Path $d 'launcher-error.log') -ErrorAction SilentlyContinue) -match 'Nessuna versione|No Lemora version') ''
Clean $d

'--- --after-pid: waits for that process to end before starting the app, and does not pass it to the app'
$d = New-Install 'wait' @('1.0.0.1') '1.0.0.1'
$wait = Start-Process $cmd -ArgumentList '/c ping -n 4 127.0.0.1 > nul' -WindowStyle Hidden -PassThru
$t0 = Get-Date
Run $d "--after-pid $($wait.Id) /c echo yes > out.txt" | Out-Null
Check 'the app starts' (Wait-File (Join-Path $d 'versions\1.0.0.1\out.txt') 15)
$sec = ((Get-Date) - $t0).TotalSeconds
Check "it waited for the other process to end (started after $([math]::Round($sec, 1)) s, at least 2 expected)" ($sec -ge 2) ''
Clean $d

'--- --after-pid with a process that does not exist: starts right away'
$d = New-Install 'nopid' @('1.0.0.1') '1.0.0.1'
$t0 = Get-Date
Run $d '--after-pid 999999 /c echo yes > out.txt' | Out-Null
Check 'starts right away'((Wait-File (Join-Path $d 'versions\1.0.0.1\out.txt') 5) -and (((Get-Date) - $t0).TotalSeconds -lt 4)) ''
Clean $d

"=== PASS: $script:pass   FAIL: $script:fail"
