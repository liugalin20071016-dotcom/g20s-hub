using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using G20SHub.Actions;

namespace G20SHub.Context;

/// <summary>一个 AI 工具窗口的配置（config.json → profiles）。</summary>
sealed class Profile
{
    public string Name;
    /// <summary>允许键的写法（见 KeySender.Spec），或 "uia" 表示用 UI Automation 点「允许」按钮。</summary>
    public string Approve;
    public string Steer;
    public string[] ApproveNames;
    /// <summary>点「允许」按钮后按钮仍在时改发的快捷键（如 Claude 卡片上标注的 ^{Enter}）。</summary>
    public string ApproveFallback;
    /// <summary>打断键的写法，或 "uia" 表示先点「停止」按钮、找不到再发 Esc。</summary>
    public string Interrupt;
    public string[] InterruptNames;
    /// <summary>选项式权限卡片的确认按钮名（如 ZCode 的「确认」）；有值时先选中允许项再点它。</summary>
    public string[] ApproveConfirm;
    /// <summary>换行键（⏯ 双击），默认 Shift+Enter。</summary>
    public string Newline;
    /// <summary>语音输入、插话、换行、标点之前，先把焦点放回这个应用的输入框（窗口底部的编辑框）。</summary>
    public bool FocusInput;
    /// <summary>
    /// 🏠 插话：发送后，AI 正在输出时消息会排队，界面上出现让它立即送达的按钮
    /// （Claude「立即传送」、Codex「引导」）——出现就点它，不打断 AI。
    /// </summary>
    public string[] SteerButtons;
    /// <summary>🔇 新建任务 / 会话要点的按钮名（UI Automation，名称完全一致）。</summary>
    public string[] NewTaskNames;
    /// <summary>⏯ 单击的模式切换："claude" = Claude 桌面版 Chat ⇄ Code；空 = 这个应用里 ⏯ 单击不做事。</summary>
    public string ModeToggle;
}

/// <summary>根据前台窗口的 exe、路径、标题匹配 profile，按配置顺序取第一个匹配的。</summary>
sealed class ContextResolver
{
    sealed class Rule
    {
        public Profile Profile;
        public string Exe, PathPart;
        public Regex Title;
    }

    readonly List<Rule> rules = new();

    public ContextResolver(Config cfg)
    {
        var profiles = cfg.Section("profiles");
        if (profiles == null)
            return;
        foreach (var kv in profiles)
        {
            if (kv.Value is not Dictionary<string, object> d)
                continue;
            string S(string key) => d.TryGetValue(key, out var v) ? v as string : null;
            string[] L(string key) => d.TryGetValue(key, out var arr) && arr is object[] a
                ? Array.ConvertAll(a, x => Convert.ToString(x)) : Array.Empty<string>();
            bool B(string key) => d.TryGetValue(key, out var v) && v is bool b && b;
            rules.Add(new Rule
            {
                Profile = new Profile
                {
                    Name = kv.Key,
                    Approve = S("approve"),
                    ApproveNames = L("approve_names"),
                    ApproveFallback = S("approve_fallback"),
                    Steer = S("steer") ?? "{Enter}",
                    Interrupt = S("interrupt") ?? "{Esc}",
                    InterruptNames = L("interrupt_names"),
                    ApproveConfirm = L("approve_confirm"),
                    Newline = S("newline") ?? "+{Enter}",
                    FocusInput = B("focus_input"),
                    SteerButtons = L("steer_buttons"),
                    NewTaskNames = L("new_task_names"),
                    ModeToggle = S("mode_toggle"),
                },
                Exe = S("match_exe"),
                PathPart = S("match_path"),
                Title = S("match_title_regex") is string t ? new Regex(t) : null,
            });
        }
    }

    /// <summary>返回匹配的 profile，没有则 null；who 用于日志（exe 和标题）。</summary>
    public Profile Resolve(IntPtr hwnd, out string who)
    {
        string path = WindowTarget.ProcessPath(hwnd);
        string exe = Path.GetFileName(path);
        string title = WindowTarget.Title(hwnd);
        who = $"{(exe.Length > 0 ? exe : "未知进程")}「{title}」";
        foreach (var r in rules)
        {
            if (r.Exe != null && !string.Equals(exe, r.Exe, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.PathPart != null && path.IndexOf(r.PathPart, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (r.Title != null && !r.Title.IsMatch(title)) continue;
            return r.Profile;
        }
        return null;
    }
}
