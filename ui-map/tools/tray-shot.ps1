# Prova nascosta del menu del vassoio: compila in ui-map\_build_new, copia in _run\restyle, avvia l'app senza finestre
# (LEMORA_HEADLESS + demo 'tray') e fotografa il menu in ui-map\_shots_tmp\tray_menu.png. Non tocca schermo, mouse, tastiera.
param([switch]$SkipBuild, [string]$Demo = 'tray')
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$shot = "$root\ui-map\_shots_tmp\tray_menu.png"
if (-not $SkipBuild) {
    dotnet build "$root\Ui\Ui.csproj" -c Release -o "$root\ui-map\_build_new" --nologo -v q
    if ($LASTEXITCODE -ne 0) { "BUILD FALLITA"; exit 1 }
}
Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\restyle\*' } | Stop-Process -Force
Start-Sleep 1
robocopy "$root\ui-map\_build_new" "$root\ui-map\_run\restyle" /E /XF Lemora.db Lemora.json /NFL /NDL /NJH /NJS /NP | Out-Null
if (Test-Path $shot) { Remove-Item $shot -Force }
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class W {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static List<IntPtr> Of(uint pid) { var l = new List<IntPtr>(); EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static bool Shot(IntPtr h, string file) {
    RECT r; GetWindowRect(h, out r); int w = r.R - r.L, hh = r.B - r.T; if (w < 20 || hh < 20) return false;
    using (var bmp = new System.Drawing.Bitmap(w, hh)) { using (var g = System.Drawing.Graphics.FromImage(bmp)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); } bmp.Save(file); }
    return true;
  }
}
'@ -ReferencedAssemblies System.Drawing
$env:LEMORA_INSTANCE_ID = 'restyle'; $env:LEMORA_HEADLESS = '1'; if ($Demo) { $env:LEMORA_DEMO = $Demo }; $env:LEMORA_NO_HOTKEY = '1'
$run = "$root\ui-map\_run\restyle"
$p = Start-Process "$run\Lemora.exe" -WorkingDirectory $run -WindowStyle Hidden -PassThru
Remove-Item Env:LEMORA_INSTANCE_ID, Env:LEMORA_HEADLESS, Env:LEMORA_DEMO, Env:LEMORA_NO_HOTKEY
$ok = $false
for ($i = 0; $i -lt 240 -and -not $ok; $i++) {
    Start-Sleep -Milliseconds 250
    foreach ($h in [W]::Of($p.Id)) {
        if ([W]::Cls($h) -notlike 'HwndWrapper*') {
            [W]::Shot($h, $shot) | Out-Null; [W]::Shot($h, $shot) | Out-Null; $ok = Test-Path $shot; if ($ok) { break }
        }
    }
}
if (-not $ok) { "finestre visibili del processo: " + (([W]::Of($p.Id) | ForEach-Object { [W]::Cls($_) }) -join ' | '); "processo uscito: $($p.HasExited)"; if (-not $p.HasExited) { $p.Refresh(); "risponde: $($p.Responding)" } }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
if ($ok) { "foto del menu: $shot" } else { "NESSUNA foto del menu" }
