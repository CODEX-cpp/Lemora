# Prova nascosta della Home responsive: avvia la copia di prova (ui-map\_run\restyle) fuori schermo con la demo "responsive",
# che porta la finestra alle grandezze del blueprint e salva una foto per ciascuna in ui-map\_shots_tmp\responsive.
# Non tocca schermo, mouse, tastiera. Usa l'ultima build in ui-map\_build_new.
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$run = "$root\ui-map\_run\restyle"
$dir = "$root\ui-map\_shots_tmp\responsive"
New-Item -ItemType Directory -Force $dir | Out-Null
Get-ChildItem $dir -Filter '*.png' | Remove-Item -Force
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\restyle\*' } | Stop-Process -Force
Start-Sleep 1
robocopy "$root\ui-map\_build_new" $run /E /XF Lemora.db Lemora.json /NFL /NDL /NJH /NJS /NP | Out-Null
$env:LEMORA_INSTANCE_ID = 'restyle'; $env:LEMORA_HEADLESS = '1'; $env:LEMORA_NO_HOTKEY = '1'
$env:LEMORA_DEMO = 'responsive'; $env:LEMORA_SHOT_DIR = $dir
# avvio con lo scheletro (come l'app vera) e tempi nel log; le foto dello scheletro alle varie grandezze le fa la demo
$env:LEMORA_SKELETON = '1'; $env:LEMORA_DIAG = '1'
# passi dell'avvio scritti subito su file: se l'avvio si blocca, l'ultima riga dice dove
$crumb = "$dir\passi-avvio.txt"; if (Test-Path $crumb) { Remove-Item $crumb -Force }; $env:LEMORA_BREADCRUMB = $crumb
$t0 = Get-Date
$p = Start-Process "$run\Lemora.exe" -WorkingDirectory $run -WindowStyle Hidden -PassThru
Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_NO_HOTKEY, Env:LEMORA_DEMO, Env:LEMORA_SHOT_DIR, Env:LEMORA_SKELETON, Env:LEMORA_DIAG, Env:LEMORA_BREADCRUMB
# si aspettano le foto (12 della Home + 5 dello scheletro) senza leggere il log mentre l'app ci scrive
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep 1
    if ($p.HasExited) { "processo uscito da solo"; break }
    if (@(Get-ChildItem $dir -Filter '*.png').Count -ge 17) { Start-Sleep 1; break }
}
"attesa: $i s"
$log = Get-ChildItem "$run\.logs" -Filter 'Lemora.log_*.md' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $p.HasExited) {
    $p.Refresh(); "processo ancora vivo, risponde: $($p.Responding), thread: $($p.Threads.Count)"
    # finestre del processo: dicono a che punto e' arrivato l'avvio (una finestra di dialogo nascosta, nessuna finestra principale...)
    Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class RW {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public static List<string> List(uint pid) { var l = new List<string>(); EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); var t = new StringBuilder(256); GetWindowText(h, t, 256); if (p == pid && t.Length > 0) l.Add(string.Format("visibile={0} titolo='{1}'", IsWindowVisible(h), t)); return true; }, IntPtr.Zero); return l; }
}
'@ -ErrorAction SilentlyContinue
    [RW]::List($p.Id)
    "ultimi passi dell'avvio:"
    if (Test-Path $crumb) { Get-Content $crumb | Where-Object { $_ -notmatch 'riflesso' } | Select-Object -Last 12 } else { "(nessun passo scritto: fermo prima di Main)" }
    Stop-Process -Id $p.Id -Force
} else { "codice di uscita: $($p.ExitCode)" }
Get-Content $log.FullName | Select-String 'DEMO responsive|`Error`|`Fatal`|Scheletro|Finestra principale|OnLaunch finito|ServerListPageView: (Loaded|Unloaded)' | Where-Object { $_.Line -match '^\[T:\d+\]\[([\d:.]+)\]' -and [datetime]::ParseExact($Matches[1], 'HH:mm:ss.fff', $null) -ge $t0.AddSeconds(-1) } | ForEach-Object { $_.Line -replace '^.*\]\s+', '' }
"foto: $((Get-ChildItem $dir -Filter '*.png').Count) in $dir"
