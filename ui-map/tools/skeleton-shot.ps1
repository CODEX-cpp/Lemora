# Prova nascosta dell'avvio con lo scheletro dentro la finestra principale: avvia la copia di prova (ui-map\_run\restyle) senza finestre
# a schermo (LEMORA_HEADLESS + LEMORA_SKELETON), fotografa la finestra principale ogni 300 ms in ui-map\_shots_tmp\skel_NN.png
# e stampa i tempi dell'avvio (LEMORA_DIAG). Non tocca schermo, mouse, tastiera. Usa l'ultima build in ui-map\_build_new.
param([string]$Build = 'ui-map\_build_new')   # cartella della build da provare, relativa alla radice del repo
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$run = "$root\ui-map\_run\restyle"
$dir = "$root\ui-map\_shots_tmp"
New-Item -ItemType Directory -Force $dir | Out-Null
Get-ChildItem $dir -Filter 'skel_*.png' | Remove-Item -Force
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\restyle\*' } | Stop-Process -Force
Start-Sleep 1
robocopy "$root\$Build" $run /E /XF Lemora.db Lemora.json /XF Lemora.db Lemora.json /NFL /NDL /NJH /NJS /NP | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class SW {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  // la finestra piu' grande del processo (la principale)
  public static IntPtr Biggest(uint pid) { IntPtr best = IntPtr.Zero; long area = 0; EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); RECT r; if (p == pid && IsWindowVisible(h) && GetWindowRect(h, out r)) { long a = (long)(r.R - r.L) * (r.B - r.T); if (a > area && r.R - r.L > 600) { area = a; best = h; } } return true; }, IntPtr.Zero); return best; }
  public static void Shot(IntPtr h, string file) {
    RECT r; GetWindowRect(h, out r); int w = r.R - r.L, hh = r.B - r.T;
    using (var bmp = new System.Drawing.Bitmap(w, hh)) { using (var g = System.Drawing.Graphics.FromImage(bmp)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); } bmp.Save(file); }
  }
}
'@ -ReferencedAssemblies System.Drawing
$env:LEMORA_INSTANCE_ID = 'restyle'; $env:LEMORA_HEADLESS = '1'; $env:LEMORA_NO_HOTKEY = '1'; $env:LEMORA_SKELETON = '1'; $env:LEMORA_DIAG = '1'; $env:LEMORA_SKELETON_SHOT = "$dir\skel_dopo.png"
if (Test-Path "$dir\skel_dopo.png") { Remove-Item "$dir\skel_dopo.png" -Force }
$t0 = Get-Date
$p = Start-Process "$run\Lemora.exe" -WorkingDirectory $run -WindowStyle Hidden -PassThru
Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_NO_HOTKEY, Env:LEMORA_SKELETON, Env:LEMORA_DIAG, Env:LEMORA_SKELETON_SHOT
$n = 0
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 300
    if ($p.HasExited) { "processo uscito da solo dopo $([int]((Get-Date) - $t0).TotalMilliseconds) ms"; break }
    $h = [SW]::Biggest($p.Id)
    if ($h -ne [IntPtr]::Zero) {
        $ms = [int]((Get-Date) - $t0).TotalMilliseconds
        $f = "$dir\skel_{0:D2}_{1}ms.png" -f $n, $ms
        try { [SW]::Shot($h, $f); $n++ } catch { "foto non riuscita a $ms ms" }
        if ([SW]::IsHungAppWindow($h)) { "a $ms ms Windows considera la finestra NON RISPONDE" }
    }
}
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
"foto fatte: $n (in $dir)"
$log = Get-ChildItem "$run\.logs" -Filter 'Lemora.log_*.md' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $log.FullName | Select-String 'START-DIAG|Fatal|Error`' | Where-Object { $_.Line -match '^\[T:\d+\]\[([\d:.]+)\]' -and [datetime]::ParseExact($Matches[1], 'HH:mm:ss.fff', $null) -ge $t0.AddSeconds(-1) } | ForEach-Object { $_.Line -replace '^.*START-DIAG: ', '' } | Where-Object { $_ -notmatch 'DECRYPT' }
