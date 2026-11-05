# Porta in primo piano la finestra "Lemora" dell'app visibile (_run\restyle), ripristinandola se e' minimizzata.
# Non si usa Process.MainWindowHandle: puo' puntare a una finestra nascosta fuori schermo.
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.Text;
public class ShowApp{
 public delegate bool P(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] static extern bool EnumWindows(P p,IntPtr l);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out R r);
 public struct R{public int L,T,Rr,B;}
 public static IntPtr Find(uint pid){IntPtr f=IntPtr.Zero;EnumWindows((h,x)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid&&IsWindowVisible(h)){var sb=new StringBuilder(100);GetWindowText(h,sb,100);if(sb.ToString()=="Lemora")f=h;}return true;},IntPtr.Zero);return f;}
}
'@
$p = Get-Process Lemora -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*_run\restyle\*' } | Select-Object -First 1
if (-not $p) { 'app non in esecuzione'; return }
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { $h = [ShowApp]::Find([uint32]$p.Id); if ($h -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 500 } }
Start-Sleep 2
if ([ShowApp]::IsIconic($h)) { [ShowApp]::ShowWindow($h, 9) | Out-Null }
[ShowApp]::SetForegroundWindow($h) | Out-Null
Start-Sleep 1
$r = New-Object ShowApp+R; [ShowApp]::GetWindowRect($h, [ref]$r) | Out-Null
"pid=$($p.Id) finestra=$h rettangolo $($r.L),$($r.T) $($r.Rr - $r.L)x$($r.B - $r.T) minimizzata=$([ShowApp]::IsIconic($h))"
