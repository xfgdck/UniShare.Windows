# ==============================================================================
# Download-Discovery-Standards.ps1 - Download official LAN Discovery Standards
# ==============================================================================

[CmdletBinding()]
param(
    [string]$TargetDir = ""
)

$ErrorActionPreference = "Continue"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir

if (-not $TargetDir) {
    $TargetDir = Join-Path $ProjectRoot "docs\standards"
}

if (-not (Test-Path $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
}

$discoveryStandards = @(
    @{
        Name = "OASIS-WSDD-1.1.pdf"
        Title = "OASIS Web Services Dynamic Discovery (WS-Discovery) Version 1.1 Specification"
        Url = "https://docs.oasis-open.org/ws-dd/discovery/1.1/os/wsdd-discovery-1.1-spec-os.pdf"
        Category = "Discovery / WS-Discovery (OASIS Standard)"
    },
    @{
        Name = "[MS-PCCRD].pdf"
        Title = "Peer Content Caching and Retrieval: Discovery Protocol (WS-Discovery based)"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-PCCRD/%5bMS-PCCRD%5d.pdf"
        Category = "Discovery / WS-Discovery (Microsoft)"
    },
    @{
        Name = "[MS-BPDP].pdf"
        Title = "BITS Peer-Caching: Peer Discovery Protocol (WS-Discovery based)"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-BPDP/%5bMS-BPDP%5d.pdf"
        Category = "Discovery / WS-Discovery (Microsoft)"
    },
    @{
        Name = "RFC6762-mDNS.txt"
        Title = "IETF RFC 6762 - Multicast DNS (mDNS) Specification"
        Url = "https://www.rfc-editor.org/rfc/rfc6762.txt"
        Category = "Discovery / mDNS (IETF Standards Track)"
    },
    @{
        Name = "RFC6763-DNS-SD.txt"
        Title = "IETF RFC 6763 - DNS-Based Service Discovery (DNS-SD) Specification"
        Url = "https://www.rfc-editor.org/rfc/rfc6763.txt"
        Category = "Discovery / DNS-SD (IETF Standards Track)"
    },
    @{
        Name = "[MS-NBTE].pdf"
        Title = "NetBIOS over TCP (NBT) Extensions Protocol Specification"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-NBTE/%5bMS-NBTE%5d.pdf"
        Category = "Discovery / NetBIOS (Microsoft)"
    },
    @{
        Name = "[MS-BRWS].pdf"
        Title = "Common Internet File System (CIFS) Browser Protocol Specification"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-BRWS/%5bMS-BRWS%5d.pdf"
        Category = "Discovery / CIFS Browser (Microsoft)"
    },
    @{
        Name = "[MS-RAP].pdf"
        Title = "Remote Administration Protocol Specification (NetServerEnum/NetShareEnum)"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-RAP/%5bMS-RAP%5d.pdf"
        Category = "Discovery / RAP Browse List (Microsoft)"
    },
    @{
        Name = "RFC1001-NetBIOS-Concepts.txt"
        Title = "IETF RFC 1001 - NetBIOS on TCP/UDP: Concepts and Methods"
        Url = "https://www.rfc-editor.org/rfc/rfc1001.txt"
        Category = "Discovery / NetBIOS (IETF Standard)"
    },
    @{
        Name = "RFC1002-NetBIOS-Specs.txt"
        Title = "IETF RFC 1002 - NetBIOS on TCP/UDP: Detailed Specifications"
        Url = "https://www.rfc-editor.org/rfc/rfc1002.txt"
        Category = "Discovery / NetBIOS (IETF Standard)"
    },
    @{
        Name = "RFC4795-LLMNR.txt"
        Title = "IETF RFC 4795 - Link-Local Multicast Name Resolution (LLMNR)"
        Url = "https://www.rfc-editor.org/rfc/rfc4795.txt"
        Category = "Discovery / LLMNR (IETF Proposed Standard)"
    }
)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [UniShare] 开始下载局域网设备自发现官方标准规范文档   " -ForegroundColor Cyan
Write-Host " 目标目录: $TargetDir" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$successCount = 0
$totalCount = $discoveryStandards.Count

for ($i = 0; $i -lt $totalCount; $i++) {
    $item = $discoveryStandards[$i]
    $destFile = Join-Path $TargetDir $item.Name
    $idx = $i + 1
    Write-Host "[$idx/$totalCount] 正在下载: $($item.Name) ($($item.Title))..." -ForegroundColor Yellow
    
    $downloaded = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & curl.exe -L -s -S -o $destFile $item.Url
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $destFile)) {
            $fi = Get-Item -LiteralPath $destFile
            if ($fi.Length -gt 1000) {
                $sizeKb = [math]::Round($fi.Length / 1KB, 1)
                $sizeMb = [math]::Round($fi.Length / 1MB, 2)
                $sizeStr = if ($sizeMb -ge 1) { "$sizeMb MB" } else { "$sizeKb KB" }
                Write-Host "   => 成功 ($sizeStr)" -ForegroundColor Green
                $downloaded = $true
                $successCount++
                break
            }
        }
        Write-Host "   => 下载重试中 ($attempt/3)..." -ForegroundColor Magenta
        Start-Sleep -Seconds 1
    }
    
    if (-not $downloaded) {
        Write-Host "   => 下载失败: $($item.Name)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " 下载完成! 成功: $successCount / $totalCount" -ForegroundColor Green
Write-Host " 标准文档存储路径: $TargetDir" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
