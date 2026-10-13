using System;
using System.Linq;
using System.Windows.Threading;
using G20SHub.UI;
using Microsoft.Win32;
using static G20SHub.Actions.KeySender;

namespace G20SHub.Actions;

/// <summary>
/// 语音输入开关（🎤 单击）。微信输入法需要**按住** Ctrl+Win 才录音（P3 实测），所以默认 latch 模式：
/// 点一下 🎤 按住快捷键不放，再点一下才松开。按住期间：
/// - 中枢的其他动作、以及任何键盘按键，都会先松开快捷键（否则会变成 Ctrl+Win+键 的系统快捷键）
/// - 输入法自己停止录音、超过 voice.max_s、暂停或退出时，自动松开
/// 真实录音状态从 Windows 麦克风使用记录读取（正在录音的程序 LastUsedTimeStop = 0）；
/// 微信输入法负责录音的进程是 wetype_update.exe。
/// 另有 tap 模式（点一下开、再点一下关），给切换式的输入法用。
/// </summary>
sealed class VoiceInput
{
    const string MicStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged";

    readonly ushort[] keys;
    readonly bool latchMode;
    readonly string micProcess;
    readonly DispatcherTimer poll, verify, watchdog;
    string[] watched = Array.Empty<string>();
    double lastScan = double.MinValue;
    bool expected, recordedDuringLatch;

    /// <summary>latch 模式下快捷键当前是否被按住（在界面线程上变化）。</summary>
    public event Action<bool> LatchChanged;

    public bool Latched { get; private set; }
    public bool Recording { get; private set; }
    public double StoppedAt { get; private set; } = double.MinValue;

    /// <summary>语音输入是否处于开启状态。</summary>
    public bool Active => latchMode ? Latched : Recording;

    public VoiceInput(Config cfg)
    {
        var names = cfg.List("voice.keys", new[] { "Ctrl", "LWin" });
        keys = names.Select(Vk).ToArray();
        latchMode = cfg.Str("voice.mode", "latch") != "tap";
        micProcess = cfg.Str("voice.mic_process", "wetype_update.exe");
        Recording = ReadRecording();
        Log.Write($"语音输入：{(latchMode ? "latch（点一下按住快捷键，再点一下松开）" : "tap（点一下快捷键开、再点一下关）")}，" +
                  $"快捷键 {string.Join("+", names)}，录音进程 {micProcess}，当前{(Recording ? "正在录音" : "未录音")}");

        poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        poll.Tick += (_, _) => Poll();
        poll.Start();

        // 1.5 秒后还没切换到预期状态，就提示（快捷键不对、当前输入法不是微信输入法等）
        verify = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        verify.Tick += (_, _) =>
        {
            verify.Stop();
            if (Recording != expected)
                Osd.Show(expected ? "⚠ 没检测到录音开始（当前输入法是微信输入法吗？）" : "⚠ 录音似乎还没停");
        };

        watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(cfg.Num("voice.max_s", 120)) };
        watchdog.Tick += (_, _) => Stop("超时");
    }

    /// <summary>🎤 单击：开 / 关语音输入。</summary>
    public void Toggle()
    {
        if (Active) Stop("再按 🎤");
        else Start();
    }

    public void Start()
    {
        if (Active)
            return;
        Log.Write("[动作] 语音输入 开");
        if (latchMode)
        {
            foreach (var k in keys) Down(k);
            Latched = true;
            recordedDuringLatch = false;
            LatchChanged?.Invoke(true);
            watchdog.Start();
        }
        else
        {
            TapKeys();
        }
        Expect(true);
        Osd.Show("🎤 正在听…", sticky: true);
    }

    /// <summary>确保语音输入已关闭；未开启时什么都不做。</summary>
    public void Stop(string reason)
    {
        if (!Active)
            return;
        Log.Write($"[动作] 语音输入 关（{reason}）");
        if (latchMode)
        {
            ReleaseKeys();
            Latched = false;
            LatchChanged?.Invoke(false);
            watchdog.Stop();
        }
        else
        {
            TapKeys();
        }
        Expect(false);
        Osd.Show(reason == "再按 🎤" ? "✓ 说完了" : $"✓ 说完了（{reason}）");
    }

    void Expect(bool recording)
    {
        expected = recording;
        verify.Stop();
        verify.Start();
    }

    void TapKeys()
    {
        foreach (var k in keys) Down(k);
        ReleaseKeys();
    }

    void ReleaseKeys()
    {
        if (keys.Contains(VK_LWIN) || keys.Contains((ushort)0x5C))
            Tap(VK_MASK);  // 遮罩 Win 键，防止松开时弹出开始菜单
        foreach (var k in keys.Reverse()) Up(k);
    }

    void Poll()
    {
        bool now = ReadRecording();
        if (now == Recording)
            return;
        Recording = now;
        Log.Write($"[状态] 麦克风{(now ? "开始录音" : "停止录音")}");
        if (now)
        {
            recordedDuringLatch |= Latched;
            return;
        }
        StoppedAt = Clock.Ms;
        // 快捷键还按着、输入法却自己停了（如长时间没说话）：松开快捷键，保持一致
        if (Latched && recordedDuringLatch)
            Stop("输入法已停止录音");
    }

    bool ReadRecording()
    {
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(MicStore);
            if (store == null)
                return false;
            // 输入法升级后路径里的版本号会变，定期重新找匹配的子键
            if (watched.Length == 0 || Clock.Ms - lastScan > 10_000)
            {
                watched = store.GetSubKeyNames()
                    .Where(n => n.EndsWith("#" + micProcess, StringComparison.OrdinalIgnoreCase)).ToArray();
                lastScan = Clock.Ms;
            }
            foreach (var name in watched)
            {
                using var k = store.OpenSubKey(name);
                if (k?.GetValue("LastUsedTimeStart") is long start && start > 0 &&
                    k.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                    return true;
            }
        }
        catch (Exception e)
        {
            Log.Write("!! 读取麦克风状态失败: " + e.Message);
        }
        return false;
    }
}
