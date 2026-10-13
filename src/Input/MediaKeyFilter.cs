using System.Collections.Generic;

namespace G20SHub.Input;

/// <summary>
/// 低级钩子里对媒体键（Windows 由 Consumer 报文翻译出的 VK）做判定：
/// 遥控器按的 → 拦截；其他设备按的 → 原生放行（不拦截、不补发，零延迟）。
/// 依据：遥控器的 Raw Input 报文总是先于系统按键到达（P0：39/39）。
/// </summary>
sealed class MediaKeyFilter
{
    readonly ConsumerTracker tracker;
    readonly double matchMs, lateWarnMs;
    readonly Dictionary<uint, int> holdRepeats = new();  // vk → 遥控器按住期间的系统连发次数
    readonly Dictionary<uint, double> passedAt = new();

    public bool DryRun;
    public int Blocked, Passed, RepeatsBlocked, LateWarnings;

    public MediaKeyFilter(ConsumerTracker tracker, double matchMs, double lateWarnMs)
    {
        this.tracker = tracker;
        this.matchMs = matchMs;
        this.lateWarnMs = lateWarnMs;
    }

    /// <summary>返回 true 表示拦截。now 必须取在取完队列中的 Raw Input 之后。</summary>
    public bool Decide(uint vk, int usage, bool up, double now, bool drainedInHook, bool injected)
    {
        string name = RemoteKeys.Name(usage) + (injected ? "*" : "");  // * = 带系统注入标记
        string dry = DryRun ? "（dry-run，未拦截）" : "";

        if (up)
        {
            if (!holdRepeats.TryGetValue(vk, out int n))
                return false;
            holdRepeats.Remove(vk);
            Log.Write($"[钩子] {name} 抬起 → 遥控器，拦截（按住期间系统连发 {n} 次）{dry}");
            return !DryRun;
        }

        if (holdRepeats.TryGetValue(vk, out int repeats))
        {
            // 遥控器按住期间 Windows 的自动连发：沿用首次判定。
            // 若遥控器其实早已释放（抬起丢失），作废并重新判定。
            var rel = tracker.LastRelease(usage);
            if (tracker.IsHeld(usage) || (rel.HasValue && now - rel.Value < 300))
            {
                holdRepeats[vk] = repeats + 1;
                RepeatsBlocked++;
                return !DryRun;
            }
            holdRepeats.Remove(vk);
        }

        var seen = tracker.LastDown(usage);
        if (seen.HasValue && now - seen.Value >= 0 && now - seen.Value < matchMs)
        {
            holdRepeats[vk] = 0;
            Blocked++;
            string how = drainedInHook ? "，钩子内取出" : "";
            Log.Write($"[钩子] {name} 按下 → 遥控器，拦截（Raw 领先 {now - seen.Value:F1} ms{how}）{dry}");
            return !DryRun;
        }

        passedAt[vk] = now;
        Passed++;
        Log.Write($"[钩子] {name} 按下 → 其他设备，原生放行");
        return false;
    }

    /// <summary>遥控器按下报文到达时调用：若同一键刚被放行，说明出现了「钩子先到」。</summary>
    public void OnRemoteDown(int usage, double ts)
    {
        if (!RemoteKeys.UsageToVk.TryGetValue(usage, out uint vk))
            return;
        if (passedAt.TryGetValue(vk, out double t) && ts - t >= 0 && ts - t < lateWarnMs)
        {
            LateWarnings++;
            passedAt.Remove(vk);
            Log.Write($"!! 钩子先到 {ts - t:F1} ms：{RemoteKeys.Name(usage)} 已被放行，系统动作已发生");
        }
    }
}
