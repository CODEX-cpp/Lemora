# Verifica che ogni controllo dell'inventario (windows\<slug>.inventory.txt) sia citato nel file windows\<slug>.md.
# Uso: powershell -File check-coverage.ps1 [slug]   (senza slug: tutti)
param([string]$Slug = '')
$dir = Join-Path $PSScriptRoot '..\windows'
$invs = Get-ChildItem $dir -Filter '*.inventory.txt' | Where-Object { (-not $Slug) -or $_.Name -eq "$Slug.inventory.txt" }
$missingTotal = 0
foreach ($inv in $invs) {
    $slug = $inv.Name -replace '\.inventory\.txt$', ''
    $md = Join-Path $dir "$slug.md"
    if (-not (Test-Path $md)) { "[$slug] MANCA il file .md"; $missingTotal++; continue }
    $text = Get-Content $md -Raw -Encoding UTF8
    # screenshot: almeno uno, e ogni immagine citata nel .md deve esistere
    $shots = [regex]::Matches($text, '\]\(\.\./screenshots/([^)]+\.png)\)') | ForEach-Object { $_.Groups[1].Value }
    $shotsMissing = @($shots | Where-Object { -not (Test-Path (Join-Path $dir "..\screenshots\$($_ -replace '/', '\')")) })
    "[$slug] screenshot citati: $(@($shots).Count) | file mancanti: $($shotsMissing.Count)" + $(if (@($shots).Count -eq 0) { '   <-- NESSUNO SCREENSHOT' } else { '' })
    $missingTotal += $shotsMissing.Count + $(if (@($shots).Count -eq 0) { 1 } else { 0 })
    $missing = @()
    foreach ($line in Get-Content $inv.FullName -Encoding UTF8) {
        $p = $line -split '\|', 3
        $name = $p[1]; $id = $p[2]
        $key = if ($name) { $name } else { $id }
        if (-not $key) { continue }               # controlli senza nome né id: coperti a mano nella sezione "Senza nome"
        if ($text -notmatch [regex]::Escape($key)) { $missing += $line }
    }
    "[$slug] controlli: $((Get-Content $inv.FullName).Count) | non citati nel .md: $($missing.Count)"
    $missing | ForEach-Object { "    MANCA: $_" }
    $missingTotal += $missing.Count
}
"TOTALE non mappati: $missingTotal"


# Riconducibilità: nessuno screenshot orfano (ogni PNG di screenshots\<slug> è citato nel .md) e ogni link a .md/PNG esiste.
$shotRoot = Join-Path $PSScriptRoot '..\screenshots'
$orph = 0; $broken = 0; $noCols = 0
foreach ($mdFile in Get-ChildItem $dir -Filter '*.md') {
    $slug = $mdFile.BaseName; $text = Get-Content $mdFile.FullName -Raw -Encoding UTF8
    if ($text -notmatch 'Dove si vede' -or $text -notmatch 'Porta a') { "[$slug] MANCANO le colonne 'Dove si vede' / 'Porta a'"; $noCols++ }
    $sd = Join-Path $shotRoot $slug
    if (Test-Path $sd) { foreach ($png in Get-ChildItem $sd -Filter *.png) { if ($text -notmatch [regex]::Escape($png.Name)) { "[$slug] ORFANO: $($png.Name)"; $orph++ } } }
    foreach ($m in [regex]::Matches($text, '\]\((?!http)([^)#]+\.(?:md|png))\)')) {
        $target = Join-Path $dir ($m.Groups[1].Value -replace '/', '\')
        if (-not (Test-Path $target)) { "[$slug] LINK ROTTO: $($m.Groups[1].Value)"; $broken++ }
    }
}
"RIEPILOGO riconducibilità: screenshot orfani $orph | link rotti $broken | file senza colonne di navigazione $noCols"
