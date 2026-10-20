# ==============================================================================
# Download-Standards.ps1 - Download all official SMB & Authentication standards
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

$standards = @(
    @{
        Name = "[MS-NLMP].pdf"
        Title = "NT LAN Manager (NTLM) Authentication Protocol"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-NLMP/%5bMS-NLMP%5d.pdf"
        Category = "Authentication / NTLM"
    },
    @{
        Name = "[MS-SMB2].pdf"
        Title = "Server Message Block (SMB) Protocol Versions 2 and 3"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-SMB2/%5bMS-SMB2%5d.pdf"
        Category = "Core Protocol / SMB2 & SMB3"
    },
    @{
        Name = "[MS-SMB].pdf"
        Title = "Server Message Block (SMB) Protocol (SMB 1.0)"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-SMB/%5bMS-SMB%5d.pdf"
        Category = "Core Protocol / SMB1"
    },
    @{
        Name = "[MS-CIFS].pdf"
        Title = "Common Internet File System (CIFS) Protocol"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-CIFS/%5bMS-CIFS%5d.pdf"
        Category = "Core Protocol / CIFS"
    },
    @{
        Name = "[MS-SPNG].pdf"
        Title = "Simple and Protected GSS-API Negotiation Mechanism (SPNEGO) Protocol Extensions"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-SPNG/%5bMS-SPNG%5d.pdf"
        Category = "Security Wrapper / SPNEGO"
    },
    @{
        Name = "[MS-KILE].pdf"
        Title = "Kerberos Protocol Extensions"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-KILE/%5bMS-KILE%5d.pdf"
        Category = "Authentication / Kerberos"
    },
    @{
        Name = "[MS-AUTHSOD].pdf"
        Title = "Authentication Services Protocols System Overview"
        Url = "https://winprotocoldoc.z19.web.core.windows.net/MS-AUTHSOD/%5bMS-AUTHSOD%5d.pdf"
        Category = "Overview / Authentication Architecture"
    },
    @{
        Name = "RFC4178-SPNEGO.txt"
        Title = "IETF RFC 4178 - The Simple and Protected GSS-API Negotiation Mechanism"
        Url = "https://www.rfc-editor.org/rfc/rfc4178.txt"
        Category = "IETF RFC / SPNEGO"
    },
    @{
        Name = "RFC4178-SPNEGO.pdf"
        Title = "IETF RFC 4178 (PDF) - The Simple and Protected GSS-API Negotiation Mechanism"
        Url = "https://www.rfc-editor.org/rfc/pdfrfc/rfc4178.txt.pdf"
        Category = "IETF RFC / SPNEGO"
    },
    @{
        Name = "RFC2743-GSSAPI.txt"
        Title = "IETF RFC 2743 - Generic Security Service Application Program Interface (GSS-API)"
        Url = "https://www.rfc-editor.org/rfc/rfc2743.txt"
        Category = "IETF RFC / GSS-API"
    },
    @{
        Name = "RFC2743-GSSAPI.pdf"
        Title = "IETF RFC 2743 (PDF) - Generic Security Service Application Program Interface (GSS-API)"
        Url = "https://www.rfc-editor.org/rfc/pdfrfc/rfc2743.txt.pdf"
        Category = "IETF RFC / GSS-API"
    },
    @{
        Name = "RFC4120-KerberosV5.txt"
        Title = "IETF RFC 4120 - The Kerberos Network Authentication Service (V5)"
        Url = "https://www.rfc-editor.org/rfc/rfc4120.txt"
        Category = "IETF RFC / Kerberos"
    },
    @{
        Name = "RFC4120-KerberosV5.pdf"
        Title = "IETF RFC 4120 (PDF) - The Kerberos Network Authentication Service (V5)"
        Url = "https://www.rfc-editor.org/rfc/pdfrfc/rfc4120.txt.pdf"
        Category = "IETF RFC / Kerberos"
    }
)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [UniShare] 开始下载 SMB 与身份认证全套官方标准文档 " -ForegroundColor Cyan
Write-Host " 目标目录: $TargetDir" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$successCount = 0
$totalCount = $standards.Count

for ($i = 0; $i -lt $totalCount; $i++) {
    $item = $standards[$i]
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
