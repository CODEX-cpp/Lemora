# Mostra cosa pesa nella cartella pubblicata (ui-map\_publish): cartelle e file piu' grandi, e quanto pesano le categorie da togliere
$p = 'C:\_dev\_projects\_repos\1Remote\ui-map\_publish'
$all = Get-ChildItem $p -Recurse -File
"Totale: {0:0} MB in {1} file" -f (($all | Measure-Object Length -Sum).Sum / 1MB), $all.Count
"--- cartelle"
Get-ChildItem $p -Directory | ForEach-Object { $s = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum; [pscustomobject]@{ Cartella = $_.Name; MB = [math]::Round($s / 1MB, 1) } } | Sort-Object MB -Descending | Format-Table -AutoSize | Out-String
"--- file piu' grandi in radice"
Get-ChildItem $p -File | Sort-Object Length -Descending | Select-Object -First 15 Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-String
"--- categorie"
"pdb: {0:0.0} MB" -f (($all | Where-Object Extension -eq '.pdb' | Measure-Object Length -Sum).Sum / 1MB)
"xml: {0:0.0} MB" -f (($all | Where-Object Extension -eq '.xml' | Measure-Object Length -Sum).Sum / 1MB)
"resources.dll: {0:0.0} MB" -f (($all | Where-Object Name -like '*.resources.dll' | Measure-Object Length -Sum).Sum / 1MB)
"--- runtimes"
Get-ChildItem "$p\runtimes" -Directory | ForEach-Object { $_.Name + ': ' + ((Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB).ToString('0.0') + ' MB' }
