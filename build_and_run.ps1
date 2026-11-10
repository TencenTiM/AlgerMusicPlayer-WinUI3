# UTF-8 编码声明，兼容简体中文 Windows 环境及含中文字符的工作目录路径
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectPath = Join-Path -Path $ScriptDir -ChildPath "AlgerMusicPlayer.WinUI\AlgerMusicPlayer.WinUI.csproj"
$ExePath = Join-Path -Path $ScriptDir -ChildPath "AlgerMusicPlayer.WinUI\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\AlgerMusicPlayer.WinUI.exe"

Write-Host "正在编译 AlgerMusicPlayer.WinUI (x64)..." -ForegroundColor Cyan
dotnet build "$ProjectPath" -c Debug -p:Platform=x64

if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $ExePath)) {
    Write-Host "编译成功，正在启动 AlgerMusicPlayer.WinUI..." -ForegroundColor Green
    Start-Process -FilePath $ExePath
}
