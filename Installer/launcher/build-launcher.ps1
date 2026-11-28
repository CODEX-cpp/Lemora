# Builds the launcher (the small Lemora.exe in Program Files) and copies it where Lemora picks it up for installation (Ui\Resources\Launcher\Lemora-launcher.bin).
# Then checks that it does not require external Visual C++ libraries (they must be inside the exe, see .cargo\config.toml).
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
Push-Location $PSScriptRoot
cargo build --release --target-dir "$repo\ui-map\_build_launcher"
$ok = $LASTEXITCODE -eq 0
Pop-Location
if (-not $ok) { throw 'launcher build failed' }
$exe = "$repo\ui-map\_build_launcher\release\Lemora.exe"
$bytes = [IO.File]::ReadAllBytes($exe)
$text = [Text.Encoding]::ASCII.GetString($bytes)
$deps = [regex]::Matches($text, '(?i)(vcruntime\d+\w*|msvcp\d+\w*|api-ms-win-crt-\w+-l\d-\d-\d)\.dll') | ForEach-Object { $_.Value.ToLower() } | Sort-Object -Unique
if ($deps) { throw "the launcher still needs external libraries: $($deps -join ', ')" }
Copy-Item $exe "$repo\Ui\Resources\Launcher\Lemora-launcher.bin" -Force
"launcher ready: {0:N0} bytes, no external Visual C++ library" -f $bytes.Length
