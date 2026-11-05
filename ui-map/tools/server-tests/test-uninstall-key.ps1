# Prova senza permessi di amministratore della voce "Lemora Server" in Impostazioni > App.
# La chiave va sotto HKCU\Software\LemoraServerTest\Uninstall (LEMORA_SERVER_UNINSTALL_ROOT) invece che in HKLM.
# Esito: PASS/FAIL per ogni controllo.  Uso: .\test-uninstall-key.ps1 [-Exe <Lemora.Server.exe>]
param([string]$Exe = 'C:\_dev\_projects\_repos\1Remote\ui-map\_build_agent_uninst\Lemora.Server.exe')
$ErrorActionPreference = 'Continue' # i rifiuti attesi scrivono su stderr
$fail = 0
function Check($name, $ok) { if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:fail++ } }

$testRoot = 'HKCU:\Software\LemoraServerTest'
$key = "$testRoot\Uninstall\LemoraServer"
$hklm = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LemoraServer'
$hklmBefore = Test-Path $hklm
Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue

# senza variabile il comando di prova si rifiuta (mai HKLM)
$env:LEMORA_SERVER_UNINSTALL_ROOT = $null
& $Exe --register-uninstall-key 2>$null | Out-Null
Check 'senza variabile il comando si rifiuta' ($LASTEXITCODE -eq 2)

# variabile non HKCU: rifiutata
$env:LEMORA_SERVER_UNINSTALL_ROOT = 'HKLM\Software\LemoraServerTest\Uninstall'
& $Exe --register-uninstall-key 2>$null | Out-Null
Check 'radice HKLM rifiutata' ($LASTEXITCODE -ne 0)

$env:LEMORA_SERVER_UNINSTALL_ROOT = 'HKCU\Software\LemoraServerTest\Uninstall'
& $Exe --register-uninstall-key --data 'D:\dati prova'
Check 'registrazione riuscita' ($LASTEXITCODE -eq 0)
$k = Get-ItemProperty $key -ErrorAction SilentlyContinue
Check 'chiave scritta' ($null -ne $k)
Check 'DisplayName' ($k.DisplayName -eq 'Lemora Server')
Check 'Publisher' ($k.Publisher -eq 'Lemora')
Check "DisplayVersion con data di build ($($k.DisplayVersion))" ($k.DisplayVersion -match '\(build \d{4}-\d\d-\d\d \d\d:\d\d\)')
Check 'InstallLocation' ($k.InstallLocation -eq (Split-Path $Exe))
Check 'DisplayIcon' ($k.DisplayIcon -eq $Exe)
Check "UninstallString ($($k.UninstallString))" ($k.UninstallString -eq "`"$Exe`" --uninstall --data `"D:\dati prova`"")
Check "EstimatedSize > 0 ($($k.EstimatedSize) KB)" ($k.EstimatedSize -gt 0)
Check 'NoModify/NoRepair' ($k.NoModify -eq 1 -and $k.NoRepair -eq 1)

# senza --data la disinstallazione usa la cartella predefinita
& $Exe --register-uninstall-key
Check 'UninstallString senza --data' ((Get-ItemProperty $key).UninstallString -eq "`"$Exe`" --uninstall")

& $Exe --remove-uninstall-key
Check 'rimozione riuscita' ($LASTEXITCODE -eq 0)
Check 'chiave rimossa' (-not (Test-Path $key))
& $Exe --remove-uninstall-key
Check 'rimozione ripetuta senza errori' ($LASTEXITCODE -eq 0)

Check 'nulla scritto in HKLM' ((Test-Path $hklm) -eq $hklmBefore)
Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue
$env:LEMORA_SERVER_UNINSTALL_ROOT = $null
if ($fail) { Write-Host "$fail controlli FALLITI"; exit 1 } else { Write-Host 'Tutti i controlli superati' }
