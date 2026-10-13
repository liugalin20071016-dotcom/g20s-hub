using System;
using System.Collections.Generic;

namespace G20SHub.Input;

/// <summary>
/// 解析遥控器 Consumer 报文（03 LL HH；03 00 00 = 释放），输出按下/抬起。
/// 遥控器同一时刻只会按住一个 Consumer 键，释放报文属于当前按住的键。
/// </summary>
sealed class ConsumerTracker
{
    readonly Dictionary<int, double> lastDown = new();
    readonly Dictionary<int, double> lastRelease = new();
    int held;

    /// <summary>usage, 是否按下, 时间戳（输入线程上触发）。</summary>
    public event Action<int, bool, double> Changed;

    public void OnReport(byte[] data, int offset, int length, double ts)
    {
        if (length < 3)
            return;
        int usage = data[offset + 1] | (data[offset + 2] << 8);
        if (usage != 0)
        {
            if (held == usage)
                return;  // 重复报文（P0 未观察到，防御）
            if (held != 0)
                Release(ts);
            held = usage;
            lastDown[usage] = ts;
            Changed?.Invoke(usage, true, ts);
        }
        else if (held != 0)
        {
            Release(ts);
        }
    }

    void Release(double ts)
    {
        int usage = held;
        held = 0;
        lastRelease[usage] = ts;
        Changed?.Invoke(usage, false, ts);
    }

    public bool IsHeld(int usage) => held == usage;

    /// <summary>该 usage 最近一次按下报文的时间，没有则返回 null。</summary>
    public double? LastDown(int usage) => lastDown.TryGetValue(usage, out var t) ? t : null;

    public double? LastRelease(int usage) => lastRelease.TryGetValue(usage, out var t) ? t : null;
}
