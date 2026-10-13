using System;
using Microsoft.Win32;

namespace G20SHub;

/// <summary>开机自启：HKCU\...\Run 里的「G20S Hub」一项（不需要管理员权限）。托盘和安装程序共用。</summary>
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "G20S Hub";

    /// <summary>已设置开机自启，并且启动的就是这个 exe。</summary>
    public static bool IsOn(string exe)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string v && string.Equals(v.Trim('"'), exe, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(string exe, bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
            k.SetValue(Name, $"\"{exe}\"");
        else if (k.GetValue(Name) != null)
            k.DeleteValue(Name);
    }
}
