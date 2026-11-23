# build.ps1 - builds, tests and publishes Isle into dist\
#   dist\Isle-Dev\        Isle.exe (framework-dependent single file, needs .NET 10 Desktop Runtime) + Music + Dev packs
#   ...-selfcontained\    the same with a self-contained Isle.exe (no runtime needed)
# Usage: .\scripts\build.ps1 [-SkipTests] [-OutputDirectory dist]
# The small Rust launcher (Isle.Launcher.exe) is built when rustc is on PATH; without it Isle.exe is started directly.
param([switch]$SkipTests, [string]$OutputDirectory = "dist")

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)   # repo root
$rustc = Get-Command rustc.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$parts = Join-Path $output ".parts"
if (Test-Path -LiteralPath $parts) { Remove-Item -LiteralPath $parts -Recurse -Force }

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

# The shell: Isle.exe as a single file, plus the hook helper.
$shell = @{ "" = Join-Path $parts "shell"; "-selfcontained" = Join-Path $parts "shell-selfcontained" }
Step "publish shell (framework-dependent)" {
    dotnet publish src\Isle\Isle.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:PublishReadyToRun=true --self-contained false -o $shell[""] -nologo -v q
}
Step "publish shell (self-contained)" {
    dotnet publish src\Isle\Isle.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:PublishReadyToRun=true --self-contained true -o $shell["-selfcontained"] -nologo -v q
}
Step "publish hook helper (framework-dependent)" {
    dotnet publish src\Isle.Hook\Isle.Hook.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o $shell[""] -nologo -v q
}
Step "publish hook helper (self-contained)" {
    dotnet publish src\Isle.Hook\Isle.Hook.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=true --self-contained true -o $shell["-selfcontained"] -nologo -v q
}

# Packs: each with its own dependencies (NuGet DLLs, the Windows SDK projection, Whisper natives), loose next to Isle.exe.
# ponytail: packs are plain IL (no ReadyToRun); publish them with R2R if their start-up time ever shows.
foreach ($pack in "Music", "Dev") {
    Step "publish $pack pack" {
        dotnet publish "src\Isle.$pack\Isle.$pack.csproj" -c Release -r win-x64 --self-contained false -o (Join-Path $parts $pack) -nologo -v q
    }
    if (-not (Test-Path -LiteralPath (Join-Path $parts "$pack\Isle.$pack.dll"))) { throw "pack output missing: $parts\$pack\Isle.$pack.dll" }
}

$packages = [ordered]@{ "Isle-Dev" = "Music", "Dev" }
$folders = @()
foreach ($package in $packages.Keys) {
    foreach ($variant in "", "-selfcontained") {
        $dir = Join-Path $output "$package$variant"
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        Copy-Item -LiteralPath $shell[$variant] -Destination $dir -Recurse
        foreach ($pack in $packages[$package]) {
            $src = Join-Path $parts $pack
            foreach ($file in Get-ChildItem -LiteralPath $src -Recurse -File -Exclude *.deps.json, *.runtimeconfig.json, *.pdb) {
                $target = Join-Path $dir $file.FullName.Substring($src.Length + 1)
                if (Test-Path -LiteralPath $target) { continue }   # the shell's (or an earlier pack's) copy wins
                New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $target
            }
        }
        # Native DLLs stay loose (Whisper looks for runtimes\win-x64\whisper.dll next to the exe). Other architectures
        # are never loaded on this machine.
        if ($launcher) { Copy-Item -LiteralPath $launcher -Destination $dir -Force }
        foreach ($arch in "win-arm64", "win-x86") {
            $native = Join-Path $dir "runtimes\$arch"
            if (Test-Path -LiteralPath $native) { Remove-Item -LiteralPath $native -Recurse -Force }
        }
        $folders += $dir
    }
}
Remove-Item -LiteralPath $parts -Recurse -Force

# Smoke checks on every folder: the right packs, and each pack really loads inside the single-file Isle.exe.
function Snapshot($dir, $state, [int]$expected = 0) {
    $png = Join-Path ([IO.Path]::GetTempPath()) "isle-smoke-$PID.png"
    Remove-Item -LiteralPath $png -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath (Join-Path $dir "Isle.exe") -ArgumentList "--snapshot", $state, "`"$png`"" -PassThru
    if (-not $p.WaitForExit(60000)) { $p.Kill(); throw "$dir`: --snapshot $state timed out" }
    $ok = $p.ExitCode -eq $expected -and ($expected -ne 0 -or (Test-Path -LiteralPath $png))
    Remove-Item -LiteralPath $png -ErrorAction SilentlyContinue
    if (-not $ok) { throw "$dir`: --snapshot $state exited $($p.ExitCode), expected $expected" }
}
function Require($dir, $file, [bool]$present) {
    if ((Test-Path -Path (Join-Path $dir $file)) -ne $present) { throw "$dir`: $file should $(if ($present) { 'be there' } else { 'not be there' })" }
}
Write-Host "== smoke checks" -ForegroundColor Cyan
foreach ($dir in $folders) {
    Require $dir "Isle.Music.dll" $true
    Require $dir "Isle.Dev.dll" $true
    Snapshot $dir "music-pill"
    Snapshot $dir "sysmon-alert"
}

Write-Host "Done:" -ForegroundColor Green
foreach ($dir in $folders) {
    $size = (Get-ChildItem -LiteralPath $dir -Recurse -File | Measure-Object Length -Sum).Sum
    "  {0}  ({1:N1} MB)" -f $dir, ($size / 1MB)
}
