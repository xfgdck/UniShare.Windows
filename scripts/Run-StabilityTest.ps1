<#
.SYNOPSIS
    UniShare 长期稳定性与并发读写压力自动化测试脚本
.DESCRIPTION
    执行阶段四自动化压测：
    1. 局域网设备自发现握手 (mDNS & WS-Discovery)
    2. 4K/8K 媒体高频随机 Seek 与 Range 分段并发吞吐
    3. 海量小文件并发写入、SHA-256 校验与删除
    4. 持续负载下内存与句柄泄漏监测
.EXAMPLE
    .\scripts\Run-StabilityTest.ps1 -DurationSeconds 15 -Threads 8
#>
param(
    [int]$DurationSeconds = 15,
    [int]$Threads = 8,
    [int]$FileSizeMb = 100,
    [string]$Mode = "all"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = "C:\Users\admin\.dotnet\dotnet.exe"
if (-not (Test-Path $dotnetPath)) {
    $dotnetPath = (Get-Command dotnet).Source
}

Write-Host ">>> 正在启动 UniShare 稳定性压测套件..." -ForegroundColor Cyan
Write-Host "    .NET 路径: $dotnetPath"
Write-Host "    项目目录: $repoRoot"

& $dotnetPath run --project "$repoRoot\src\UniShare.StressTest\UniShare.StressTest.csproj" -- `
    --mode $Mode `
    --duration $DurationSeconds `
    --threads $Threads `
    --file-size $FileSizeMb

if ($LASTEXITCODE -eq 0) {
    Write-Host "`n[PASS] 压测执行完毕，所有指标符合阶段四稳定性与性能要求！" -ForegroundColor Green
} else {
    Write-Host "`n[FAIL] 压测发现异常，请检查上述日志信息！" -ForegroundColor Red
}
