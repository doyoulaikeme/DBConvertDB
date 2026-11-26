# 一键发布到 dist\，产出可直接拷贝给实施现场的目录
param(
    [string]$Configuration = "Release",
    [switch]$KeepDist
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

if ((Test-Path $dist) -and (-not $KeepDist)) {
    Remove-Item $dist -Recurse -Force
}
if (-not (Test-Path $dist)) {
    New-Item -ItemType Directory $dist | Out-Null
}

Write-Host "== 发布 CLI =="
dotnet publish (Join-Path $root "src\DbConvert.Cli\DbConvert.Cli.csproj") -c $Configuration -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw "CLI 发布失败" }

Write-Host "== 发布 GUI =="
dotnet publish (Join-Path $root "src\DbConvert.Gui\DbConvert.Gui.csproj") -c $Configuration -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw "GUI 发布失败" }

# 厂商驱动目录与示例配置
$providers = Join-Path $dist "providers"
if (-not (Test-Path $providers)) { New-Item -ItemType Directory $providers | Out-Null }
Set-Content -Path (Join-Path $providers "放置说明.txt") -Encoding UTF8 -Value @"
把数据库厂商的 ADO.NET 驱动放在这里（或直接放在上一级程序目录）：
  达梦 DM     : DmProvider.dll
  金仓 Kingbase: Kdbndp.dll
其余 MySQL / PostgreSQL / Oracle / SQL Server / SQLite 驱动已随包提供。
厂商驱动受各自授权限制，不要上传到公开仓库；详细说明见 README.md。
"@
Copy-Item (Join-Path $root "providers\README.md") $providers -ErrorAction SilentlyContinue

$samples = Join-Path $dist "samples"
if (-not (Test-Path $samples)) { New-Item -ItemType Directory $samples | Out-Null }
Copy-Item (Join-Path $root "samples\*.json") $samples -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "README.md") $dist -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "发布完成：$dist"
Get-ChildItem $dist -Filter "DbConvert.*.exe" | ForEach-Object { Write-Host ("  " + $_.Name) }
