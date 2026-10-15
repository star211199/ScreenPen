# ============================================================================
#  ScreenPen build script  (PowerShell 5+ / Windows)
#
#  Produces a single self-contained ScreenPen.exe with no external runtime
#  dependency, using only the .NET Framework compiler that ships with Windows.
#
#  Usage:  powershell -ExecutionPolicy Bypass -File build\build.ps1
# ============================================================================

$ErrorActionPreference = 'Stop'

$Root      = Split-Path -Parent $PSScriptRoot
$Src       = Join-Path $Root 'src'
$Assets    = Join-Path $Root 'assets'
$Build     = Join-Path $Root 'build'
$Obj       = Join-Path $Build 'obj'
$Out       = Join-Path $Root 'dist'
$OutExe    = Join-Path $Out 'ScreenPen.exe'

Write-Host ''
Write-Host '=== ScreenPen build ===' -ForegroundColor Cyan

# ---- locate the compiler ---------------------------------------------------
$csc = $null
foreach ($p in @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'))) {
    if (Test-Path $p) { $csc = $p; break }
}
if (-not $csc) {
    # Fall back to any Roslyn compiler that may be on the machine.
    $cand = Get-ChildItem -Path (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio') `
                          -Filter csc.exe -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
    if ($cand) { $csc = $cand.FullName }
}
if (-not $csc) { throw 'No C# compiler (csc.exe) found. Install .NET Framework 4.x.' }
Write-Host "compiler : $csc"

New-Item -ItemType Directory -Force -Path $Obj, $Out | Out-Null

# ---- icons -----------------------------------------------------------------
# The .ico files are embedded as manifest resources so the app carries its own
# artwork and needs no files next to the EXE.
$appIco  = Join-Path $Assets 'app.ico'
$trayIco = Join-Path $Assets 'tray.ico'
if (-not (Test-Path $appIco))  { throw "missing icon: $appIco" }
if (-not (Test-Path $trayIco)) { throw "missing icon: $trayIco" }
Write-Host "icons    : app.ico, tray.ico"

# ---- cursor artwork --------------------------------------------------------
# The brush glyph is embedded so the app carries its own pointer artwork and
# works even when launched from a different directory. The .cs source already
# holds the same path data as a fallback, so a missing file is not fatal - but
# a stale, malformed override would silently replace the shipped cursor, so the
# path data is validated here rather than trusted.
$brushSvg = Join-Path $Assets 'brush.svg'
if (Test-Path $brushSvg) {
    $svgText = Get-Content -Raw -Encoding UTF8 $brushSvg
    $dCount = ([regex]::Matches($svgText, '<path[^>]*?\bd\s*=')).Count
    if ($dCount -lt 1) { throw "brush.svg contains no <path d=...> element: $brushSvg" }
    if ($svgText -notmatch 'viewBox\s*=\s*"\s*0\s+0\s+1024\s+1024\s*"') {
        Write-Host 'brush svg: WARNING - viewBox is not "0 0 1024 1024"; cursor scale may be off' -ForegroundColor Yellow
    }
    Write-Host "brush svg: brush.svg ($dCount path(s))"
} else {
    Write-Host 'brush svg: brush.svg not found, using the path data compiled into BrushCursor.cs' -ForegroundColor Yellow
}

# ---- version resource (optional) -------------------------------------------
# Requires rc.exe from the Windows SDK; skipped cleanly when unavailable.
$resFile = $null
$rc = Get-Command rc.exe -ErrorAction SilentlyContinue
if ($rc) {
    $rcPath = Join-Path $Build 'version.rc'
    if (Test-Path $rcPath) {
        Push-Location $Build
        try {
            & $rc.Source /nologo /fo (Join-Path $Obj 'version.res') $rcPath 2>&1 | Out-Null
            if (Test-Path (Join-Path $Obj 'version.res')) {
                $resFile = Join-Path $Obj 'version.res'
                Write-Host 'version  : embedded via rc.exe'
            }
        } catch { Write-Host 'version  : rc.exe failed, continuing without it' -ForegroundColor Yellow }
        finally { Pop-Location }
    }
} else {
    Write-Host 'version  : rc.exe not found, using PE metadata only' -ForegroundColor DarkGray
}

# ---- sources ---------------------------------------------------------------
$sources = Get-ChildItem -Path $Src -Filter *.cs -Recurse | ForEach-Object { $_.FullName }
if (-not $sources -or $sources.Count -eq 0) { throw "no .cs sources under $Src" }
Write-Host "sources  : $($sources.Count) file(s)"

# ---- compile ---------------------------------------------------------------
$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll'
)

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/unsafe-',
    '/codepage:65001',
    "/out:$OutExe",
    "/win32icon:$appIco",
    "/win32manifest:$(Join-Path $Build 'app.manifest')",
    "/resource:$appIco,app.ico",
    "/resource:$trayIco,tray.ico"
)
if (Test-Path $brushSvg) { $cscArgs += "/resource:$brushSvg,assets\brush.svg" }
if ($resFile) { $cscArgs += $resFile }
foreach ($r in $refs) { $cscArgs += "/reference:$r" }
$cscArgs += $sources

Write-Host ''
Write-Host '--- compiling ---' -ForegroundColor Cyan
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compilation failed (exit $LASTEXITCODE)" }

if (-not (Test-Path $OutExe)) { throw 'compiler reported success but produced no EXE' }

# ---- report ----------------------------------------------------------------
$size = (Get-Item $OutExe).Length
$hash = (Get-FileHash $OutExe -Algorithm SHA256).Hash
Write-Host ''
Write-Host '=== BUILD OK ===' -ForegroundColor Green
Write-Host ("exe      : {0}" -f $OutExe)
Write-Host ("size     : {0:N0} bytes ({1:N1} KB)" -f $size, ($size / 1KB))
Write-Host ("sha256   : {0}" -f $hash)
Write-Host ''
