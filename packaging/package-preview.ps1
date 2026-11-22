<#
.SYNOPSIS
  Build the "preview" download for friends: a self-contained win-x64 Flyweight folder with ffmpeg,
  a README and licence notices, zipped into artifacts\Flyweight-<version>-win-x64.zip.

.EXAMPLE
  pwsh packaging\package-preview.ps1 -Version 0.2.0-preview
  pwsh packaging\package-preview.ps1 -Version 0.2.0-preview -FfmpegDir "C:\ffmpeg\bin"

  -FfmpegDir: folder with ffmpeg.exe and ffprobe.exe. Defaults to the Chocolatey install. Its parent folder's
  LICENSE / README.txt (gyan.dev builds) are shipped as ffmpeg\LICENSE.txt / ffmpeg\README.txt.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $FfmpegDir = "C:\ProgramData\chocolatey\lib\ffmpeg\tools\ffmpeg\bin"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$name = "Flyweight-$Version-win-x64"
$out = Join-Path $repo "artifacts\$name"
$zip = Join-Path $repo "artifacts\$name.zip"

foreach ($exe in "ffmpeg.exe", "ffprobe.exe") {
    if (-not (Test-Path (Join-Path $FfmpegDir $exe))) { throw "$exe not found in $FfmpegDir (use -FfmpegDir)" }
}
$ffmpegRoot = Split-Path $FfmpegDir -Parent
$ffLicense = Join-Path $ffmpegRoot "LICENSE"
$ffReadme = Join-Path $ffmpegRoot "README.txt"
if (-not (Test-Path $ffLicense)) { throw "ffmpeg's LICENSE not found next to $FfmpegDir; the GPL requires shipping it." }

# Versions for the notices.
$ffVersion = (& (Join-Path $FfmpegDir "ffmpeg.exe") -hide_banner -version | Select-Object -First 1) -replace '^ffmpeg version (\S+).*$', '$1'
$ffSource = if (Test-Path $ffReadme) { (Select-String -Path $ffReadme -Pattern '^Source Code:\s*(\S+)').Matches.Groups[1].Value } else { "https://ffmpeg.org/download.html" }
$csproj = Get-Content (Join-Path $repo "src\Flyweight.App\Flyweight.App.csproj") -Raw
$libvlc = [regex]::Match($csproj, 'VideoLAN\.LibVLC\.Windows" Version="([^"]+)"').Groups[1].Value
$libvlcsharp = [regex]::Match($csproj, 'Include="LibVLCSharp" Version="([^"]+)"').Groups[1].Value

Write-Host "Publishing Flyweight $Version (self-contained win-x64)…"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $repo "src\Flyweight.App\Flyweight.App.csproj") -c Release -r win-x64 --self-contained true `
    -p:Version=$($Version -replace '-.*$', '') -p:InformationalVersion=$Version -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
# The LibVLC package ships 32- and 64-bit copies; this is a win-x64 build, so the 32-bit one is dead weight.
$x86 = Join-Path $out "libvlc\win-x86"
if (Test-Path $x86) { Remove-Item $x86 -Recurse -Force }

# ffmpeg + its licence (GPL v3: licence text and source location must travel with the binaries).
$ffOut = Join-Path $out "ffmpeg"
New-Item -ItemType Directory -Force $ffOut | Out-Null
Copy-Item (Join-Path $FfmpegDir "ffmpeg.exe"), (Join-Path $FfmpegDir "ffprobe.exe") $ffOut
Copy-Item $ffLicense (Join-Path $ffOut "LICENSE.txt")
if (Test-Path $ffReadme) { Copy-Item $ffReadme (Join-Path $ffOut "README.txt") }

# README, licence, notices.
(Get-Content (Join-Path $PSScriptRoot "README.txt") -Raw).Replace("{VERSION}", $Version) | Set-Content (Join-Path $out "README.txt") -Encoding utf8
Copy-Item (Join-Path $repo "LICENSE") (Join-Path $out "LICENSE.txt")
(Get-Content (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt") -Raw).
    Replace("{FFMPEG_VERSION}", $ffVersion).Replace("{FFMPEG_SOURCE}", $ffSource).
    Replace("{LIBVLC_VERSION}", $libvlc).Replace("{LIBVLCSHARP_VERSION}", $libvlcsharp) |
    Set-Content (Join-Path $out "THIRD-PARTY-NOTICES.txt") -Encoding utf8

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
$mb = [math]::Round((Get-Item $zip).Length / 1MB)
Write-Host "Done: $zip ($mb MB)"
Write-Host "  ffmpeg $ffVersion · LibVLC $libvlc · LibVLCSharp $libvlcsharp"
