using System;
using System.IO;
using System.Reflection;

namespace G20SHub;

/// <summary>
/// 数据（config.json、ring.json、logs\）放在哪，按顺序判断：
/// 1. --data 目录：用指定目录
/// 2. 开发：exe 在项目的 bin\ 里（上一级有 build.ps1）→ 项目根目录
/// 3. 免安装版：exe 旁边有 config.json → exe 所在目录
/// 4. 安装版：%LOCALAPPDATA%\G20SHub
/// 数据目录里没有 config.json 时，写出 exe 内置的默认配置。
/// </summary>
static class AppPaths
{
    public static readonly string ExeDir = TrimSlash(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory));

    static string TrimSlash(string dir) => dir.Length > 3 ? dir.TrimEnd('\\') : dir;  // 保留 C:\ 这种根目录

    /// <summary>
    /// 重启 / 开机自启用的 exe：固定是目录里的 G20SHub.exe
    /// （编译器可能把运行中的旧 exe 改名挪开，不能用自身路径）；被改过名时用自身路径。
    /// </summary>
    public static string Exe
    {
        get
        {
            var fixedPath = Path.Combine(ExeDir, "G20SHub.exe");
            return File.Exists(fixedPath) ? fixedPath : Assembly.GetExecutingAssembly().Location;
        }
    }

    public static string DataDir { get; private set; }

    /// <summary>开发 / 免安装 / 安装版 / 指定目录，写进日志。</summary>
    public static string Mode { get; private set; }

    public static string Config => Path.Combine(DataDir, "config.json");
    public static string Ring => Path.Combine(DataDir, "ring.json");
    public static string LogFile => Path.Combine(DataDir, "logs", "events.log");

    public static void Init(string[] args)
    {
        int i = Array.IndexOf(args, "--data");
        var devRoot = Path.GetFullPath(Path.Combine(ExeDir, ".."));
        if (i >= 0 && i + 1 < args.Length)
            (DataDir, Mode) = (Path.GetFullPath(args[i + 1]), "指定目录");
        else if (File.Exists(Path.Combine(devRoot, "build.ps1")) && Directory.Exists(Path.Combine(devRoot, "src")))
            (DataDir, Mode) = (devRoot, "开发");
        else if (File.Exists(Path.Combine(ExeDir, "config.json")))
            (DataDir, Mode) = (ExeDir, "免安装");
        else
            (DataDir, Mode) = (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "G20SHub"), "安装版");

        Directory.CreateDirectory(DataDir);
        if (!File.Exists(Config))
            WriteDefaultConfig();
    }

    /// <summary>把编译时内置的 config.json 写到数据目录。</summary>
    static void WriteDefaultConfig()
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("G20SHub.config.json");
        if (res == null)
            return;
        using var f = File.Create(Config);
        res.CopyTo(f);
    }
}
