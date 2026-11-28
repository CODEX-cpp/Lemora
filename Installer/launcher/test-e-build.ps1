# launcher tests (cargo test), then build and copy to Ui\Resources\Launcher (build-launcher.ps1)
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
Push-Location $PSScriptRoot
cargo test --target-dir "$repo\ui-map\_build_launcher" 2>&1 | Select-String -Pattern 'test result|FAILED|panicked|error'
$ok = $LASTEXITCODE -eq 0
Pop-Location
if (-not $ok) { "LAUNCHER TESTS FAILED"; exit 1 }
& (Join-Path $PSScriptRoot 'build-launcher.ps1')
