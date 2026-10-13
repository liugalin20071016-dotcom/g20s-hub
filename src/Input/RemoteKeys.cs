using System.Collections.Generic;

namespace G20SHub.Input;

/// <summary>遥控器按键常量：接收器 VID 和各键的 usage（G20S Pro 实测）。换遥控器时改这里。</summary>
static class RemoteKeys
{
    public const int RemoteVendorId = 0x4842;

    // Consumer usage
    public const int Mic = 0x00CF, Mute = 0x00E2, VolUp = 0x00E9, VolDown = 0x00EA,
        PlayPause = 0x00CD, Prev = 0x00B6, Next = 0x00B5, Back = 0x0224, Home = 0x0223;

    // ⏻ 电源键：多数遥控器走 System Control 接口（Generic Desktop 页），少数走 Consumer 的 Power / Sleep，两种都认
    public const int SysPowerDown = 0x81, SysSleep = 0x82, SysWakeUp = 0x83;
    public const int ConsumerPower = 0x0030, ConsumerSleep = 0x0032;

    public static bool IsPower(int consumerUsage) => consumerUsage is ConsumerPower or ConsumerSleep;

    public static string SystemName(int usage) => usage switch
    {
        SysPowerDown => "⏻关机（0x81）",
        SysSleep => "⏻睡眠（0x82）",
        SysWakeUp => "⏻唤醒（0x83）",
        _ => $"0x{usage:X2}",
    };

    /// <summary>Windows 把这些 Consumer usage 翻译成的系统按键（VK）。</summary>
    public static readonly Dictionary<uint, int> VkToUsage = new()
    {
        [0xAD] = Mute,       // VK_VOLUME_MUTE
        [0xAE] = VolDown,    // VK_VOLUME_DOWN
        [0xAF] = VolUp,      // VK_VOLUME_UP
        [0xB3] = PlayPause,  // VK_MEDIA_PLAY_PAUSE
        [0xB1] = Prev,       // VK_MEDIA_PREV_TRACK
        [0xB0] = Next,       // VK_MEDIA_NEXT_TRACK
        [0xA6] = Back,       // VK_BROWSER_BACK
        [0xAC] = Home,       // VK_BROWSER_HOME
    };

    public static readonly Dictionary<int, uint> UsageToVk = Invert(VkToUsage);

    static readonly Dictionary<int, string> Names = new()
    {
        [Mic] = "🎤语音", [Mute] = "🔇静音", [VolUp] = "🔊音量+", [VolDown] = "🔉音量-",
        [PlayPause] = "⏯播放", [Prev] = "⏮上一曲", [Next] = "⏭下一曲", [Back] = "↩返回", [Home] = "🏠主页",
        [ConsumerPower] = "⏻电源", [ConsumerSleep] = "⏻睡眠",
    };

    public static string Name(int usage) =>
        Names.TryGetValue(usage, out var n) ? n : $"0x{usage:X4}";

    static Dictionary<int, uint> Invert(Dictionary<uint, int> src)
    {
        var d = new Dictionary<int, uint>();
        foreach (var kv in src)
            d[kv.Value] = kv.Key;
        return d;
    }
}
