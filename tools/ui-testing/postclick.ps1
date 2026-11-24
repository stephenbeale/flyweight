param([int]$ProcessId, [int]$X, [int]$Y, [int]$ToX = -1, [int]$ToY = -1)
# Click (or drag) inside a window by POSTING mouse messages to the child control under the point.
# Window-relative pixel coords (as in a PrintWindow capture). Never moves the real cursor or takes focus.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PM {
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
      var child = ChildWindowFromPointEx(cur, p, 1 | 4); // skip invisible + transparent
      if (child == IntPtr.Zero || child == cur) { client = p; return cur; }
      cur = child;
    }
  }
}
"@
[PM]::SetProcessDPIAware() | Out-Null
$best = [IntPtr]::Zero; $bestArea = 0
$cb = [PM+EnumProc]{ param($h, $l)
  $procId = 0; [PM]::GetWindowThreadProcessId($h, [ref]$procId) | Out-Null
  if ($procId -eq $ProcessId -and [PM]::IsWindowVisible($h)) {
    $r = New-Object PM+RECT; [PM]::GetWindowRect($h, [ref]$r) | Out-Null
    $a = ($r.R - $r.L) * ($r.B - $r.T)
    if ($a -gt $script:bestArea) { $script:bestArea = $a; $script:best = $h }
  }
  return $true }
[PM]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$r = New-Object PM+RECT; [PM]::GetWindowRect($best, [ref]$r) | Out-Null
function LParam($p) { [IntPtr](($p.Y -shl 16) -bor ($p.X -band 0xFFFF)) }
$c = New-Object PM+POINT
$target = [PM]::Deepest($best, $r.L + $X, $r.T + $Y, [ref]$c)
[PM]::PostMessage($target, 0x0200, [IntPtr]0, (LParam $c)) | Out-Null   # WM_MOUSEMOVE
[PM]::PostMessage($target, 0x0201, [IntPtr]1, (LParam $c)) | Out-Null   # WM_LBUTTONDOWN
if ($ToX -ge 0) {
  for ($i = 1; $i -le 10; $i++) {
    $p = New-Object PM+POINT; $p.X = $c.X + ($ToX - $X) * $i / 10; $p.Y = $c.Y + ($ToY - $Y) * $i / 10
    [PM]::PostMessage($target, 0x0200, [IntPtr]1, (LParam $p)) | Out-Null
    Start-Sleep -Milliseconds 30
  }
  $c.X += $ToX - $X; $c.Y += $ToY - $Y
}
[PM]::PostMessage($target, 0x0202, [IntPtr]0, (LParam $c)) | Out-Null   # WM_LBUTTONUP
Start-Sleep -Milliseconds 500
"posted to $target at $($c.X),$($c.Y)"
