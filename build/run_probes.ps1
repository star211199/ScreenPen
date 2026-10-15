# Rebuilds and runs every in-process probe against the current sources.
#
# The probes compile the real app sources, so a probe that still passes after a
# layout change is meaningful evidence; one that was never recompiled is not.
# Always run this before claiming a change is verified.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot\..

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = @(
  '/reference:System.dll'
  '/reference:System.Core.dll'
  '/reference:System.Drawing.dll'
  '/reference:System.Windows.Forms.dll'
)
$srcs = @(
  'src\Config.cs', 'src\SettingsStore.cs', 'src\Native.cs', 'src\OverlayForm.cs'
  'src\BrushCursor.cs', 'src\RegionSelectForm.cs', 'src\AppContext2.cs'
  'src\SettingsForm.cs', 'src\Shell.cs', 'src\PinForm.cs'
)

$probes = @(
  'tools_probe', 'brush_probe', 'region_probe', 'hotkey_conflict_probe'
  'smooth_probe', 'follow_probe', 'footer_probe', 'layout_probe', 'measure_ui'
  'hotkey_record_probe', 'text_focus_probe', 'hostile_focus_probe', 'pin_probe'
  'typing_probe'
)

$failed = @()
foreach ($p in $probes) {
  $exe = "build\obj\$p.exe"
  Remove-Item $exe -ErrorAction SilentlyContinue
  $out = & $csc /nologo /target:exe /platform:x64 /codepage:65001 /out:$exe `
    $refs "build\$p.cs" $srcs 2>&1
  if ($LASTEXITCODE -ne 0) {
    Write-Host "=== $p : COMPILE FAILED ===" -ForegroundColor Red
    $out | Select-Object -First 8 | ForEach-Object { Write-Host "    $_" }
    $failed += "$p (compile)"
    continue
  }

  $run = & $exe 2>&1
  $code = $LASTEXITCODE
  # Keep the full transcript: follow_probe samples the live desktop, so it can
  # flake under load, and a truncated tail hides which check actually moved.
  $log = "build\obj\$p.log"
  $run | Set-Content -Path $log -Encoding UTF8

  # -CaseSensitive matters: every probe prints a lowercase "0 failed" summary,
  # and a case-insensitive match on 'FAILED' flags all of them as broken.
  $bad = @($run | Select-String -CaseSensitive -Pattern '\[FAIL\]|FAILED|Exception|错误')
  if ($code -eq 0 -and $bad.Count -eq 0) {
    $summary = ($run | Select-String -Pattern 'ALL PASS|passed|PASS' |
      Select-Object -Last 1)
    $line = if ($summary) { $summary.Line.Trim() } else { 'ok' }
    Write-Host ("{0,-22} PASS   {1}" -f $p, $line) -ForegroundColor Green
  } else {
    Write-Host ("{0,-22} FAIL (exit {1})" -f $p, $code) -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "    $_" }
    Write-Host "    (full transcript: $log)"
    $failed += $p
  }
}

Write-Host ''
if ($failed.Count -eq 0) {
  Write-Host 'ALL PROBES PASSED' -ForegroundColor Green
  exit 0
}
Write-Host ("FAILED: " + ($failed -join ', ')) -ForegroundColor Red
exit 1
