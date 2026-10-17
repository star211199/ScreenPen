# Compile and run the visual diagnostics
# (render_pin / render_ui / render_scroll / render_gifresult / render_cursor).
#
# These are NOT part of run_probes.ps1: they exist to produce PNGs for a human to
# look at, not to assert anything. Without this script the only way to run them
# is a hand-typed compiler command, which is exactly the kind of thing that rots
# (the flags below are not guessable: Program.cs has its own Main and must be
# excluded, and Gif.cs/GifRecorder.cs need the WPF assemblies).
#
# Usage:
#   .\build\run_render.ps1                     # render_pin
#   .\build\run_render.ps1 render_ui render_scroll render_gifresult render_cursor
#
# Output lands in build\shots\render\.
param([string[]] $Names = @('render_pin'))
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot\..

# Same discovery order as build.ps1 / run_probes.ps1, so all three agree.
$csc = $null
foreach ($p in @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'))) {
  if (Test-Path $p) { $csc = $p; break }
}
if (-not $csc) { throw 'No C# compiler (csc.exe) found. Install .NET Framework 4.x.' }

$fwDir = Split-Path -Parent $csc
$refs = @(
  '/reference:System.dll'
  '/reference:System.Core.dll'
  '/reference:System.Drawing.dll'
  '/reference:System.Windows.Forms.dll'
  "/reference:$fwDir\WPF\PresentationCore.dll"
  "/reference:$fwDir\WPF\WindowsBase.dll"
  "/reference:$fwDir\System.Xaml.dll"
)

# Every source except Program.cs: it declares Main, and so does each renderer.
$srcs = Get-ChildItem -Path 'src' -Filter *.cs -Recurse |
  Where-Object { $_.Name -ne 'Program.cs' } |
  ForEach-Object { $_.FullName }

foreach ($n in $Names) {
  $exe = "build\obj\$n.exe"
  Remove-Item $exe -ErrorAction SilentlyContinue
  $out = & $csc /nologo /target:exe /platform:x64 /codepage:65001 /out:$exe `
    $refs "build\$n.cs" $srcs 2>&1
  if ($LASTEXITCODE -ne 0) {
    Write-Host "=== $n : COMPILE FAILED ===" -ForegroundColor Red
    $out | Select-Object -First 12 | ForEach-Object { Write-Host "    $_" }
    continue
  }
  Write-Host "=== $n ===" -ForegroundColor Cyan
  & $exe
}
