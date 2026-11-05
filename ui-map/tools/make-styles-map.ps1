# Genera ui-map\STILI-CONDIVISI.md contando gli stili e i colori XAML usati dalle schermate dell'app.
# Uso: powershell -NoProfile -File make-styles-map.ps1
$root = 'C:\_dev\_projects\_repos\1Remote'
$files = Get-ChildItem "$root\Ui" -Recurse -Filter *.xaml | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$styleRx = [regex]'Style="\{(?:StaticResource|DynamicResource)\s+([^}\s]+)\}"'
$brushRx = [regex]'\{(?:StaticResource|DynamicResource)\s+(\w*(?:Brush|Color)\w*)\}'
$styles = @{}; $brushes = @{}
foreach ($f in $files) {
    $rel = $f.FullName.Substring("$root\Ui\".Length)
    $text = Get-Content $f.FullName -Raw -Encoding UTF8
    foreach ($m in $styleRx.Matches($text)) { $k = $m.Groups[1].Value; if (-not $styles[$k]) { $styles[$k] = @{ n = 0; f = @{} } }; $styles[$k].n++; $styles[$k].f[$rel] = 1 }
    foreach ($m in $brushRx.Matches($text)) { $k = $m.Groups[1].Value; if (-not $brushes[$k]) { $brushes[$k] = @{ n = 0; f = @{} } }; $brushes[$k].n++; $brushes[$k].f[$rel] = 1 }
}
# dove sono definiti gli stili piu' usati
$top = $styles.GetEnumerator() | Sort-Object { $_.Value.n } -Descending | Select-Object -First 30
$defs = @{}
$srcFiles = Get-ChildItem $root -Recurse -Include *.xaml -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
foreach ($t in $top) {
    $hit = $srcFiles | Select-String -SimpleMatch ('x:Key="' + $t.Key + '"') -List | Select-Object -First 1
    $defs[$t.Key] = if ($hit) { $hit.Path.Substring($root.Length + 1) + ':' + $hit.LineNumber } else { '(non trovato)' }
}
$o = New-Object System.Text.StringBuilder
[void]$o.AppendLine('# Stili e colori condivisi')
[void]$o.AppendLine('')
[void]$o.AppendLine("> Generato da ``tools\make-styles-map.ps1`` il $(Get-Date -Format 'yyyy-MM-dd HH:mm') leggendo $($files.Count) file XAML sotto ``Ui\``. Conta le **referenze** ``Style=""{StaticResource ...}""`` e i riferimenti a pennelli/colori: dice dove ritoccare una volta sola per cambiare molte schermate. Non misura l'aspetto grafico e non conta gli stili impliciti (per tipo, senza chiave) né quelli applicati dal codice.")
[void]$o.AppendLine('')
[void]$o.AppendLine('## Stili con chiave più usati')
[void]$o.AppendLine('| Stile | Referenze | File che lo usano | Definito in (file:riga) | Schermate di esempio |')
[void]$o.AppendLine('|---|---|---|---|---|')
foreach ($t in $top) {
    $ex = ($t.Value.f.Keys | Sort-Object | Select-Object -First 3) -join ', '
    [void]$o.AppendLine("| ``$($t.Key)`` | $($t.Value.n) | $($t.Value.f.Count) | ``$($defs[$t.Key])`` | $ex |")
}
[void]$o.AppendLine('')
[void]$o.AppendLine('## Pennelli e colori più usati')
[void]$o.AppendLine('| Risorsa | Referenze | File che la usano |')
[void]$o.AppendLine('|---|---|---|')
foreach ($b in ($brushes.GetEnumerator() | Sort-Object { $_.Value.n } -Descending | Select-Object -First 25)) {
    [void]$o.AppendLine("| ``$($b.Key)`` | $($b.Value.n) | $($b.Value.f.Count) |")
}
[void]$o.AppendLine('')
[void]$o.AppendLine('## Come leggerla per il restyling')
[void]$o.AppendLine('- Uno stile con molte referenze e un solo punto di definizione si ritocca nel file indicato in "Definito in" e cambia in tutte le schermate che lo usano.')
[void]$o.AppendLine('- I colori vengono dal tema attivo (`ThemeService.cs`, vedi [03e-opzioni-tema](windows/03e-opzioni-tema.md)): un pennello come `PrimaryTextBrush` cambia con il tema scelto.')
[void]$o.AppendLine('- Gli stili senza chiave (applicati per tipo di controllo) e i valori scritti a mano nei singoli XAML (colori, dimensioni, margini) non sono in questa tabella: vanno cercati schermata per schermata.')
$o.ToString() | Set-Content "$root\ui-map\STILI-CONDIVISI.md" -Encoding UTF8
"stili con chiave distinti: $($styles.Count) | pennelli/colori distinti: $($brushes.Count) | file letti: $($files.Count)"
