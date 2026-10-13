# 打包发布版 → dist\
#   G20SHub-<版本>-Setup.exe      安装程序（只给当前用户装，不需要管理员权限）
#   G20SHub-<版本>-portable.zip   免安装版（解压即用，配置和日志放在 exe 旁边）
# 和 build.ps1 一样只用 .NET SDK 自带的 Roslyn + 系统自带的 .NET Framework 4.8，不用第三方打包工具。
param(
    [string]$Publisher = 'Galin',   # 「设置 → 应用」里显示的发布者
    [string]$Url = ''               # 项目主页（可选，发布到 GitHub 后填仓库地址）
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
$gen = Join-Path $root 'obj'

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage, $gen | Out-Null

# 1) 程序本体
& (Join-Path $root 'build.ps1') -Out $stage
$app = Join-Path $stage 'G20SHub.exe'

# 2) 安装程序：把程序本体作为资源嵌进去
$sdkLine = (& dotnet --list-sdks) | Select-Object -Last 1
if ($sdkLine -notmatch '^(\S+) \[(.+)\]$') { throw "找不到 .NET SDK" }
$csc = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"

$info = Join-Path $gen 'SetupInfo.cs'
Set-Content $info -Encoding UTF8 @"
using System.Reflection;
[assembly: AssemblyTitle("G20S Hub 安装程序")]
[assembly: AssemblyProduct("G20S Hub")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Galin · MIT License")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
namespace G20SHub.Setup;
static class SetupInfo
{
    public const string Version = "$version";
    public const string Publisher = "$Publisher";
    public const string Url = "$Url";
}
"@

$payload = @("-resource:$app,payload/G20SHub.exe")
$license = Join-Path $root 'LICENSE'
if (Test-Path $license) { $payload += "-resource:$license,payload/LICENSE.txt" }

$refs = 'mscorlib', 'System', 'System.Core', 'System.Xaml',
        'WPF\WindowsBase', 'WPF\PresentationCore', 'WPF\PresentationFramework' |
        ForEach-Object { "-r:$fw\$_.dll" }
$src = (Join-Path $root 'setup\Setup.cs'), (Join-Path $root 'src\UI\Theme.cs'), (Join-Path $root 'src\AutoStart.cs'), $info
$setup = Join-Path $dist "G20SHub-$version-Setup.exe"

& dotnet $csc -nologo -langversion:latest -nostdlib -noconfig -codepage:65001 -utf8output `
    -target:winexe -optimize+ -warn:4 "-out:$setup" `
    "-win32icon:$root\assets\G20SHub.ico" "-resource:$root\assets\icon-256.png,setup/icon.png" `
    @payload @refs @src
if ($LASTEXITCODE -ne 0) { throw "安装程序编译失败（退出码 $LASTEXITCODE）" }

# 3) 免安装版：exe 旁边放 config.json，程序就把配置、圆环设置和日志都放在这个文件夹里
$name = "G20SHub-$version-portable"
$folder = Join-Path $stage $name
New-Item -ItemType Directory -Force $folder | Out-Null
Copy-Item $app, (Join-Path $root 'config.json') $folder
Copy-Item (Join-Path $root 'setup\portable-README.txt') (Join-Path $folder 'README.txt')
if (Test-Path $license) { Copy-Item $license (Join-Path $folder 'LICENSE.txt') }
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $folder -DestinationPath $zip

Remove-Item -Recurse -Force $stage
Write-Host ""
Write-Host "发布文件（版本 $version）："
Get-ChildItem $dist -File | ForEach-Object { Write-Host ("  {0,-36} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB)) }
