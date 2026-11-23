# build.ps1 - builds, tests and publishes Isle into dist\
#   dist\Isle\                 Isle.exe, framework-dependent single file (needs the .NET 10 Desktop Runtime)
#   dist\Isle-selfcontained\   the same with a self-contained Isle.exe (no runtime needed)
# Usage: .\scripts\build.ps1 [-SkipTests] [-OutputDirectory dist]
# The small Rust launcher (Isle.Launcher.exe) is built when rustc is on PATH; without it Isle.exe is started directly.
param([switch]$SkipTests, [string]$OutputDirectory = "dist")

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)   # repo root
$rustc = Get-Command rustc.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName

function Step($name, [scriptblock]$body) {
    Write-Host "== $name" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
}

Step "build" { dotnet build Isle.sln -c Release -nologo -v q }
if (-not $SkipTests) { Step "test" { dotnet test Isle.sln -c Release -nologo --no-build -v q } }

$launcher = $null
if ($rustc) {
    $launcherDir = New-Item -ItemType Directory -Path "target\isle-launcher" -Force
    $launcher = Join-Path $launcherDir.FullName "Isle.Launcher.exe"
    Step "build Rust launcher" {
        & $rustc --edition=2024 --target x86_64-pc-windows-msvc --crate-name isle_launcher -C opt-level=s -C strip=symbols src\Isle.Launcher\main.rs -o $launcher
    }
}
else {
    Write-Host "== rustc not found: skipping Isle.Launcher.exe (run Isle.exe directly)" -ForegroundColor Yellow
}

# Isle.exe as a single file, plus the hook helper.
$folders = @()
foreach ($selfContained in $false, $true) {
    $variant = if ($selfContained) { "self-contained" } else { "framework-dependent" }
    $dir = Join-Path $output $(if ($selfContained) { "Isle-selfcontained" } else { "Isle" })
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    $sc = if ($selfContained) { "true" } else { "false" }
    Step "publish Isle ($variant)" {
        dotnet publish src\Isle\Isle.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:PublishReadyToRun=true --self-contained $sc -o $dir -nologo -v q
    }
    Step "publish hook helper ($variant)" {
        if ($selfContained) { dotnet publish src\Isle.Hook\Isle.Hook.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=true --self-contained true -o $dir -nologo -v q }
        else { dotnet publish src\Isle.Hook\Isle.Hook.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o $dir -nologo -v q }
    }
    if ($launcher) { Copy-Item -LiteralPath $launcher -Destination $dir -Force }
    $folders += $dir
}

# Smoke checks: Isle.exe renders the music mini player and a Dev tab.
function Snapshot($dir, $state) {
    $png = Join-Path ([IO.Path]::GetTempPath()) "isle-smoke-$PID.png"
    Remove-Item -LiteralPath $png -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath (Join-Path $dir "Isle.exe") -ArgumentList "--snapshot", $state, "`"$png`"" -PassThru
    if (-not $p.WaitForExit(60000)) { $p.Kill(); throw "$dir`: --snapshot $state timed out" }
    $ok = $p.ExitCode -eq 0 -and (Test-Path -LiteralPath $png)
    Remove-Item -LiteralPath $png -ErrorAction SilentlyContinue
    if (-not $ok) { throw "$dir`: --snapshot $state exited $($p.ExitCode)" }
}
Write-Host "== smoke checks" -ForegroundColor Cyan
foreach ($dir in $folders) { Snapshot $dir "music-mini"; Snapshot $dir "sysmon-alert" }

Write-Host "Done:" -ForegroundColor Green
foreach ($dir in $folders) {
    $size = (Get-ChildItem -LiteralPath $dir -Recurse -File | Measure-Object Length -Sum).Sum
    "  {0}  ({1:N1} MB)" -f $dir, ($size / 1MB)
}
