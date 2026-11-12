# AlgerMusicPlayer WinUI 3 自研安装包与绿色便携版一键构建脚本
# 兼容简体中文 Windows 环境及中文路径

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppProj = Join-Path $ScriptDir "AlgerMusicPlayer.WinUI\AlgerMusicPlayer.WinUI.csproj"
$InstallerDir = Join-Path $ScriptDir "AlgerMusicPlayer.Installer"
$InstallerProj = Join-Path $InstallerDir "AlgerMusicPlayer.Installer.csproj"
$DistDir = Join-Path $ScriptDir "dist"
$PortableOutDir = Join-Path $DistDir "AlgerMusicPlayer-win-x64"
$PortableZipPath = Join-Path $DistDir "AlgerMusicPlayer-Portable-x64.zip"
$PayloadZipPath = Join-Path $InstallerDir "payload.zip"
$InstallerPublishDir = Join-Path $InstallerDir "bin\Release\Publish"
$FinalSetupExePath = Join-Path $DistDir "AlgerMusicPlayer-Setup-x64.exe"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  AlgerMusicPlayer WinUI 3 自研安装包构建工具 (v5.1.3)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. 停止运行中的实例避免文件占用
Stop-Process -Name "AlgerMusicPlayer.WinUI" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "mpv" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "AlgerMusicPlayer-Setup-x64" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

if (Test-Path -LiteralPath $PortableOutDir) {
    Remove-Item -LiteralPath $PortableOutDir -Recurse -Force
}
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

# 清理 WinUI 3 Release 增量缓存，确保打包的 XBF 界面布局与 DLL 100% 同步最新源码
dotnet clean "$AppProj" -c Release -r win-x64 -p:Platform=x64 --nologo -v q | Out-Null

# 2. 发布自包含 WinUI 3 主程序
Write-Host "`n[1/4] 正在发布 WinUI 3 自包含主程序 (win-x64)..." -ForegroundColor Green
dotnet publish "$AppProj" -c Release -r win-x64 -p:Platform=x64 --self-contained true -o "$PortableOutDir"
if ($LASTEXITCODE -ne 0) {
    throw "WinUI 3 主程序发布失败！"
}

# 清理主程序目录中冗余的 .pdb 符号文件以减小安装包体积
Get-ChildItem -LiteralPath $PortableOutDir -Filter "*.pdb" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

# 3. 打包绿色便携版 ZIP 与安装器内嵌 Payload
Write-Host "`n[2/4] 正在生成压缩载荷包 (payload.zip)..." -ForegroundColor Green
if (Test-Path -LiteralPath $PortableZipPath) {
    Remove-Item -LiteralPath $PortableZipPath -Force
}
if (Test-Path -LiteralPath $PayloadZipPath) {
    Remove-Item -LiteralPath $PayloadZipPath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $PortableOutDir,
    $PortableZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Copy-Item -LiteralPath $PortableZipPath -Destination $PayloadZipPath -Force

# 4. 编译并发布单文件自研安装程序 AlgerMusicPlayer-Setup-x64.exe
Write-Host "`n[3/4] 正在编译自研单文件安装器 (AlgerMusicPlayer-Setup-x64.exe)..." -ForegroundColor Green
if (Test-Path -LiteralPath $InstallerPublishDir) {
    Remove-Item -LiteralPath $InstallerPublishDir -Recurse -Force
}

dotnet publish "$InstallerProj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$InstallerPublishDir"
if ($LASTEXITCODE -ne 0) {
    throw "自研安装器编译失败！"
}

$BuiltSetupExe = Join-Path $InstallerPublishDir "AlgerMusicPlayer-Setup-x64.exe"
Copy-Item -LiteralPath $BuiltSetupExe -Destination $FinalSetupExePath -Force

# 清理临时 payload.zip
Remove-Item -LiteralPath $PayloadZipPath -Force -ErrorAction SilentlyContinue

$SetupSizeMB = [Math]::Round((Get-Item -LiteralPath $FinalSetupExePath).Length / 1MB, 2)
$ZipSizeMB = [Math]::Round((Get-Item -LiteralPath $PortableZipPath).Length / 1MB, 2)

Write-Host "`n[4/4] 打包完成！" -ForegroundColor Green
Write-Host "  -> 标准自研安装包: $FinalSetupExePath ($SetupSizeMB MB)" -ForegroundColor Yellow
Write-Host "  -> 绿色免安装压缩包: $PortableZipPath ($ZipSizeMB MB)" -ForegroundColor Yellow
Write-Host "  -> 绿色免安装目录:   $PortableOutDir" -ForegroundColor Yellow
