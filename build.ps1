# 编译 G20S Hub → bin\G20SHub.exe（-Out 可指定其他目录，打包时用）
# 用 .NET SDK 自带的 Roslyn 编译器，直接引用 Windows 自带的 .NET Framework 4.8 程序集：
# 不用 NuGet、不下载任何包；生成的 exe 只依赖系统自带的 .NET Framework。
# 中枢正在运行时也能编译（编译器会把占用中的旧 exe 改名挪开）；之后重新运行，新版本会自动接管旧实例。
param([string]$Out = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'

$sdkLine = (& dotnet --list-sdks) | Select-Object -Last 1
if ($sdkLine -notmatch '^(\S+) \[(.+)\]$') { throw "找不到 .NET SDK（dotnet --list-sdks 无输出）" }
$csc = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"
if (-not (Test-Path $csc)) { throw "找不到编译器 $csc" }

# 版本 = VERSION 文件 + 编译时间。编译时间显示在托盘、提示条和日志里，用来确认正在运行的是哪个版本
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$stamp = Get-Date -Format 'MM-dd HH:mm'
$gen = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $gen | Out-Null
$info = Join-Path $gen 'BuildInfo.cs'
Set-Content $info -Encoding UTF8 @"
using System.Reflection;
[assembly: AssemblyTitle("G20S Hub")]
[assembly: AssemblyProduct("G20S Hub")]
[assembly: AssemblyDescription("G20S 飞鼠遥控器 → AI 对话 / 编程遥控中枢")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Galin · MIT License")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version ($stamp)")]
namespace G20SHub;
static class BuildInfo
{
    public const string Version = "$version";
    public const string Time = "$stamp";
    public const string Label = "$version · $stamp";
}
"@

$refs = 'mscorlib', 'System', 'System.Core', 'System.Xaml', 'System.Web.Extensions',
        'System.Windows.Forms', 'System.Drawing',
        'WPF\WindowsBase', 'WPF\PresentationCore', 'WPF\PresentationFramework',
        'WPF\UIAutomationClient', 'WPF\UIAutomationTypes' |
        ForEach-Object { "-r:$fw\$_.dll" }
$src = @(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object FullName) + $info

New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $Out 'G20SHub.exe'

# config.json 内置进 exe：安装版第一次运行时写到数据目录作为默认配置
$cscArgs = @($csc, '-nologo', '-langversion:latest', '-nostdlib', '-noconfig', '-codepage:65001', '-utf8output',
             '-target:winexe', '-optimize+', '-warn:4', "-out:$exe",
             "-win32icon:$root\assets\G20SHub.ico", "-resource:$root\config.json,G20SHub.config.json") + $refs + $src
$output = & dotnet @cscArgs 2>&1
$output | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0 -and ($output -match 'CS2012')) {
    # 中枢运行中时，第一次写 exe 偶尔因文件被占用失败（CS2012，编译器会顺手把旧 exe 改名挪开），重试一次即可
    Write-Host "（exe 被运行中的中枢占用，重试一次）"
    & dotnet @cscArgs
}
if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }

# 清掉之前被改名挪开的旧 exe（还在运行的删不掉，跳过）
Get-ChildItem $Out -Filter '*_G20SHub.exe' -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item $_.FullName -ErrorAction Stop } catch { }
}

Write-Host "已生成 $exe · 版本 $version · $stamp"
if (Get-Process G20SHub -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Out, 'OrdinalIgnoreCase') }) {
    Write-Host "中枢正在运行旧版本：重新运行 $exe，新版本会自动接管。"
}
