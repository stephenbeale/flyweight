param([int]$ProcessId, [int]$X, [int]$Y, [int]$Vk = 0, [switch]$HoverOnly)
# Post a key press (or just a mouse move, for hover) to the child control under a window-relative point.
# Never touches the real keyboard, cursor or focus.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PK {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static IntPtr Deepest(IntPtr top, int sx, int sy, out POINT client) {
    IntPtr cur = top;
    while (true) {
      var p = new POINT { X = sx, Y = sy }; ScreenToClient(cur, ref p);
      var child = ChildWindowFromPointEx(cur, p, 1 | 4);
      if (child == IntPtr.Zero || child == cur) { client = p; return cur; }
      cur = child;
    }
  }
}
"@
[PK]::SetProcessDPIAware() | Out-Null
$best = [IntPtr]::Zero; $bestArea = 0
$cb = [PK+EnumProc]{ param($h, $l)
  $procId = 0; [PK]::GetWindowThreadProcessId($h, [ref]$procId) | Out-Null
  if ($procId -eq $ProcessId -and [PK]::IsWindowVisible($h)) {
    $r = New-Object PK+RECT; [PK]::GetWindowRect($h, [ref]$r) | Out-Null
    $a = ($r.R - $r.L) * ($r.B - $r.T)
    if ($a -gt $script:bestArea) { $script:bestArea = $a; $script:best = $h }
  }
  return $true }
[PK]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$r = New-Object PK+RECT; [PK]::GetWindowRect($best, [ref]$r) | Out-Null
$c = New-Object PK+POINT
$target = [PK]::Deepest($best, $r.L + $X, $r.T + $Y, [ref]$c)
if ($HoverOnly) {
  [PK]::PostMessage($target, 0x0200, [IntPtr]0, [IntPtr](($c.Y -shl 16) -bor ($c.X -band 0xFFFF))) | Out-Null
} else {
  [PK]::PostMessage($target, 0x0100, [IntPtr]$Vk, [IntPtr]1) | Out-Null          # WM_KEYDOWN
  [PK]::PostMessage($target, 0x0101, [IntPtr]$Vk, [IntPtr]0xC0000001) | Out-Null # WM_KEYUP
}
Start-Sleep -Milliseconds 500
"posted to $target"
