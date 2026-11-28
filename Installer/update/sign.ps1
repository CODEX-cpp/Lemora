# Signs a file (usually lemora-<version>-update-package.zip) with this PC's private key and writes <file>.sig next to it (base64, 64 bytes).
param([Parameter(Mandatory)][string]$File)
$keyFile = Join-Path $env:USERPROFILE '.lemora-updater.key'
if (-not (Test-Path $keyFile)) { throw "Private key ${keyFile} missing: run new-keys.ps1 first" }
Add-Type -AssemblyName System.Security
$priv = [System.Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String((Get-Content $keyFile -Raw).Trim()), $null, 'CurrentUser')
$key = [System.Security.Cryptography.CngKey]::Import($priv, [System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
$k = [System.Security.Cryptography.ECDsaCng]::new($key)
$sig = $k.SignData([IO.File]::ReadAllBytes($File), [System.Security.Cryptography.HashAlgorithmName]::SHA256)
[IO.File]::WriteAllText("$File.sig", [Convert]::ToBase64String($sig))
"Signed: $File.sig ($($sig.Length) bytes)"
