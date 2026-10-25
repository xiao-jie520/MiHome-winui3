<#
.SYNOPSIS
    构建米家控制台（WinUI 3）自包含发布目录与 MSI 安装包。

.EXAMPLE
    pwsh ./build-installer.ps1
    pwsh ./build-installer.ps1 -Version 1.0.1

.NOTES
    要求：.NET 8 SDK、Windows 10 1809+。
    WiX 5（免费版）通过 dotnet tool 自动安装，无需 Visual Studio。
#>
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "==> 1/3 发布自包含应用到 publish/" -ForegroundColor Cyan
dotnet publish "src/MiHome.App/MiHome.App.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained `
    -p:Platform=x64 `
    -o "publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

Write-Host "==> 2/3 准备 WiX 工具链" -ForegroundColor Cyan
$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    dotnet tool install --global wix --version 5.0.2
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}
wix extension add -g WixToolset.UI.wixext/5.0.2 2>$null

Write-Host "==> 3/3 生成 MSI 安装包" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "artifacts" | Out-Null
wix build "installer/Product.wxs" `
    -ext WixToolset.UI.wixext `
    -culture zh-CN `
    -d "Version=$Version" `
    -o "artifacts/MiHome-Setup-$Version.msi"
if ($LASTEXITCODE -ne 0) { throw "wix build 失败" }

$msi = Get-Item "artifacts/MiHome-Setup-$Version.msi"
Write-Host ""
Write-Host ("完成：{0}  ({1:N1} MB)" -f $msi.FullName, ($msi.Length / 1MB)) -ForegroundColor Green
