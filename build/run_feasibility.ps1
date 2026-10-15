# Runs the standalone feasibility probes.
#
# These are NOT app probes: each one is a self-contained experiment that answers
# a single "is this feature even possible here?" question, and they compile
# WITHOUT the app sources. They back the conclusions in
# `ScreenPen功能增强调研报告.md`, so they are kept runnable rather than being
# one-off scratch files - a roadmap claim with no repeatable evidence is just an
# opinion.
#
# They live outside run_probes.ps1 on purpose:
#   * gif_probe needs extra WPF references (see below), the others do not;
#   * they measure OS capabilities, not app behaviour, so a failure here means
#     "this machine/OS build lacks the API", not "the app regressed".
#
# Usage: pwsh -NoProfile -File build\run_feasibility.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot\..

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fw = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319"
$base = @(
  '/reference:System.dll'
  '/reference:System.Core.dll'
  '/reference:System.Drawing.dll'
  '/reference:System.Windows.Forms.dll'
)

# gif_probe drives the WPF GIF encoder. WindowsBase.dll lives under WPF\, NOT in
# the Framework64 root, and System.Xaml.dll must come from Framework64 (the GAC
# copy does not exist as a file -> error CS0006).
$wpf = @(
  "/reference:$fw\WPF\PresentationCore.dll"
  "/reference:$fw\WPF\WindowsBase.dll"
  "/reference:$fw\System.Xaml.dll"
)

# name -> extra references beyond $base
$probes = [ordered]@{
  'ocr_probe2'   = @()
  'feature_probe'= @()
  'roadmap_probe'= @()
  'gif_probe'    = $wpf
}

$failed = @()
foreach ($p in $probes.Keys) {
  $exe = "build\obj\$p.exe"
  Remove-Item $exe -ErrorAction SilentlyContinue
  $refs = $base + $probes[$p]
  $out = & $csc /nologo /target:exe /platform:x64 /codepage:65001 /out:$exe `
    $refs "build\$p.cs" 2>&1
  if ($LASTEXITCODE -ne 0) {
    Write-Host ("{0,-16} COMPILE FAILED" -f $p) -ForegroundColor Red
    $out | Select-Object -First 6 | ForEach-Object { Write-Host "    $_" }
    $failed += "$p (compile)"
    continue
  }

  $run = & $exe 2>&1
  $code = $LASTEXITCODE
  $run | Set-Content -Path "build\obj\$p.log" -Encoding UTF8

  # -CaseSensitive for the same reason as run_probes.ps1: the summaries print a
  # lowercase "0 failed".
  $bad = @($run | Select-String -CaseSensitive -Pattern '\[FAIL\]|FAILED|Exception|错误')
  if ($code -eq 0 -and $bad.Count -eq 0) {
    $summary = ($run | Select-String -Pattern 'RESULT|PASS' | Select-Object -Last 1)
    $line = if ($summary) { $summary.Line.Trim() } else { 'ok' }
    Write-Host ("{0,-16} PASS   {1}" -f $p, $line) -ForegroundColor Green
  } else {
    Write-Host ("{0,-16} FAIL (exit {1})" -f $p, $code) -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "    $_" }
    $failed += $p
  }
}

Write-Host ''
if ($failed.Count -eq 0) {
  Write-Host 'ALL FEASIBILITY PROBES PASSED' -ForegroundColor Green
  exit 0
}
Write-Host ("FAILED: " + ($failed -join ', ')) -ForegroundColor Red
exit 1
