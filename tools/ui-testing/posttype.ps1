param([int]$ProcessId, [int]$X, [int]$Y, [string]$Text, [switch]$Enter)
# Click into the control under a window-relative point and type text into it with posted messages (no real input).
& "$PSScriptRoot\postclick.ps1" -ProcessId $ProcessId -X $X -Y $Y | Out-Null
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PT {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetFocus();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize, flags; public IntPtr active, focus, capture, menuOwner, moveSize, caret; public int l, t, r, b; }
  [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
}
"@
$p = Get-Process -Id $ProcessId
$tid = $p.Threads[0].Id
$info = New-Object PT+GUITHREADINFO; $info.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($info)
[PT]::GetGUIThreadInfo([uint32]$tid, [ref]$info) | Out-Null
$target = $info.focus
if ($target -eq [IntPtr]::Zero) { "no focused control"; return }
# Select all, then type.
[PT]::PostMessage($target, 0x00B1, [IntPtr]0, [IntPtr](-1)) | Out-Null   # EM_SETSEL 0..-1
foreach ($ch in $Text.ToCharArray()) { [PT]::PostMessage($target, 0x0102, [IntPtr][int]$ch, [IntPtr]1) | Out-Null } # WM_CHAR
if ($Enter) {
  [PT]::PostMessage($target, 0x0100, [IntPtr]0x0D, [IntPtr]1) | Out-Null
  [PT]::PostMessage($target, 0x0101, [IntPtr]0x0D, [IntPtr]0xC0000001) | Out-Null
}
Start-Sleep -Milliseconds 600
"typed into $target"
