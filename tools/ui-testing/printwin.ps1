param([int]$ProcessId, [string]$Out)
# Capture a window's own pixels (even if it's behind other windows) without focusing it or sending any input.
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PW {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@
[PW]::SetProcessDPIAware() | Out-Null
# The largest visible top-level window of the process (not a tooltip).
$best = [IntPtr]::Zero; $bestArea = 0
$cb = [PW+EnumProc]{ param($h, $l)
  $procId = 0; [PW]::GetWindowThreadProcessId($h, [ref]$procId) | Out-Null
  if ($procId -eq $ProcessId -and [PW]::IsWindowVisible($h)) {
    $r = New-Object PW+RECT; [PW]::GetWindowRect($h, [ref]$r) | Out-Null
    $a = ($r.R - $r.L) * ($r.B - $r.T)
    if ($a -gt $script:bestArea) { $script:bestArea = $a; $script:best = $h }
  }
  return $true }
[PW]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$r = New-Object PW+RECT; [PW]::GetWindowRect($best, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
[PW]::PrintWindow($best, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $bmp.Save($Out)
"saved $Out ($($bmp.Width)x$($bmp.Height))"
