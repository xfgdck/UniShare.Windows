# ==============================================================================
# UniShare.Windows - Build Inno Setup Installer
# ==============================================================================

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [UniShare] Building Inno Setup Installer                 " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Locate dotnet SDK
$dotnetExe = $null

$pathCmd = Get-Command dotnet -ErrorAction SilentlyContinue
$pathDotnet = if ($pathCmd) { $pathCmd.Source } else { $null }

$candidateDotnets = @(
    "C:\Users\admin\.dotnet\dotnet.exe",
    "C:\Program Files\dotnet\dotnet.exe",
    $pathDotnet
)

foreach ($c in $candidateDotnets) {
    if ($c -and (Test-Path $c)) {
        $dotnetInfo = & $c --list-sdks 2>&1
        if ($dotnetInfo -match "8\.") {
            $dotnetExe = $c
            break
        }
    }
}

if (-not $dotnetExe) {
    $dotnetExe = "dotnet"
}
Write-Host "[1/4] Using .NET SDK: $dotnetExe" -ForegroundColor Green

# 2. Locate Inno Setup ISCC.exe
$isccExe = $null

$pathIsccCmd = Get-Command iscc -ErrorAction SilentlyContinue
$pathIscc = if ($pathIsccCmd) { $pathIsccCmd.Source } else { $null }

$candidateIsccs = @(
    "C:\Program Files (x86)\Inno Setup 6\iscc.exe",
    "C:\Program Files\Inno Setup 6\iscc.exe",
    $pathIscc
)

foreach ($c in $candidateIsccs) {
    if ($c -and (Test-Path $c)) {
        $isccExe = $c
        break
    }
}

if (-not $isccExe) {
    Write-Error "Inno Setup 6 (iscc.exe) not found."
    exit 1
}
Write-Host "[2/4] Using Inno Setup Compiler: $isccExe" -ForegroundColor Green

# 3. dotnet publish
$publishDir = Join-Path $ProjectRoot "bin\publish-fdd"
$projectPath = Join-Path $ProjectRoot "src\UniShare.UI\UniShare.UI.csproj"

if (Test-Path $publishDir) {
    Write-Host "Cleaning publish directory: $publishDir" -ForegroundColor Gray
    Remove-Item -Path $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "[3/4] Publishing UniShare.UI ($Configuration, win-x64)..." -ForegroundColor Yellow
& $dotnetExe publish $projectPath -c $Configuration -r "win-x64" --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code: $LASTEXITCODE"
    exit $LASTEXITCODE
}

# 4. Inno Setup compile
$issFile = Join-Path $ScriptDir "installer.iss"
Write-Host "[4/4] Compiling installer with Inno Setup..." -ForegroundColor Yellow

$distDir = Join-Path $ProjectRoot "dist"
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

& $isccExe "/Qp" "/DMyAppVersion=$Version" $issFile
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed with exit code: $LASTEXITCODE"
    exit $LASTEXITCODE
}

# 5. Output summary
$outputInstaller = Join-Path $distDir "UniShare-v$Version-Setup-x64.exe"
if (Test-Path $outputInstaller) {
    $item = Get-Item $outputInstaller
    $sizeMB = [math]::Round($item.Length / 1MB, 2)
    $hashObj = Get-FileHash -Path $outputInstaller -Algorithm SHA256
    $hash = $hashObj.Hash

    Write-Host ""
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host " UniShare Installer built successfully!" -ForegroundColor Green
    Write-Host " Output file: $($item.FullName)" -ForegroundColor White
    Write-Host " File size:   $sizeMB MB ($($item.Length) bytes)" -ForegroundColor White
    Write-Host " SHA-256:     $hash" -ForegroundColor Gray
    Write-Host " Features:    Admin required, auto-downloads .NET 8 if missing" -ForegroundColor Gray
    Write-Host "==========================================================" -ForegroundColor Green
} else {
    Write-Error "Output installer not found: $outputInstaller"
    exit 1
}