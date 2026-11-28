# Creates ONCE the key pair used to sign Lemora updates (ECDSA P-256).
# - the PRIVATE key stays only on this PC, encrypted with the Windows account of whoever creates it (DPAPI): %USERPROFILE%\.lemora-updater.key
# - the PUBLIC key is printed here and goes into the app code (Ui\Setup\UpdateKey.cs): it is public by definition.
# If the private key file already exists it is NOT overwritten: losing it would mean no longer being able to sign updates for already installed apps.
$keyFile = Join-Path $env:USERPROFILE '.lemora-updater.key'
if (Test-Path $keyFile) { "The private key already exists: $keyFile (left untouched)"; return }
Add-Type -AssemblyName System.Security
$k = [System.Security.Cryptography.ECDsaCng]::new(256)
$priv = $k.Key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
$pub = $k.Key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
$prot = [System.Security.Cryptography.ProtectedData]::Protect($priv, $null, 'CurrentUser')
[IO.File]::WriteAllText($keyFile, [Convert]::ToBase64String($prot))
"Private key saved (encrypted for this Windows user): $keyFile"
"Public key (to embed in the app):"
[Convert]::ToBase64String($pub)
