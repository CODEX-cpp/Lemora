# chiude i processi lasciati da prove interrotte (Edge nascosto e server di prova) e cancella le cartelle temporanee
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'console-test-|pairing-test-|edge-e2e-' } | ForEach-Object {
    "chiudo $($_.Name) $($_.ProcessId)"
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}
Start-Sleep 2
Get-ChildItem $env:TEMP -Directory | Where-Object { $_.Name -match '^(console-test-|pairing-test-|edge-e2e-)' } | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
'pulito'
