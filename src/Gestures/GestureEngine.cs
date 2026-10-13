using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace G20SHub.Gestures;

enum Gesture { Tap, Double, Long, HoldStart, HoldEnd, Repeat }

[Flags]
enum Needs { None = 0, Double = 1, Long = 2, Hold = 4, Repeat = 8 }

/// <summary>
/// 把按下/抬起变成手势。只在键声明了需要时才引入等待：
/// 只有 tap 的键按下即触发；有 double 的键单击要等 double_ms；有 long 的键松开时才确定是单击。
/// 运行在界面线程上（计时器用 DispatcherTimer），无需加锁。
/// 安全：连发最多持续 MaxRepeatMs，万一丢了「松开」也会自己停（P5 教训：丢失松开导致方向键无限连发）。
/// </summary>
sealed class GestureEngine
{
    const double MaxRepeatMs = 10_000;

    sealed class State
    {
        public bool Down, LongFired, WaitingSecond, DoubleConsumed;
        public double RepeatStart;
        public DispatcherTimer LongTimer, DoubleTimer, RepeatTimer;
    }

    readonly Dictionary<string, Needs> specs;
    readonly Dictionary<string, State> states = new();
    readonly TimeSpan doubleWait, longWait, repeatDelay, repeatEvery;

    public event Action<string, Gesture> Fired;

    public GestureEngine(Config cfg, Dictionary<string, Needs> specs)
    {
        this.specs = specs;
        doubleWait = TimeSpan.FromMilliseconds(cfg.Num("timing.double_ms", 280));
        longWait = TimeSpan.FromMilliseconds(cfg.Num("timing.long_ms", 500));
        repeatDelay = TimeSpan.FromMilliseconds(cfg.Num("timing.repeat_delay_ms", 400));
        repeatEvery = TimeSpan.FromMilliseconds(cfg.Num("timing.repeat_ms", 80));
    }

    public void OnDown(string key)
    {
        var s = Get(key);
        if (s.Down)
            return;
        s.Down = true;
        s.LongFired = false;
        var needs = Spec(key);

        if (needs.HasFlag(Needs.Hold))
            Fire(key, Gesture.HoldStart);

        if (needs.HasFlag(Needs.Repeat))
        {
            Fire(key, Gesture.Tap);
            s.RepeatStart = Clock.Ms;
            s.RepeatTimer ??= NewTimer(() =>
            {
                if (!s.Down || Clock.Ms - s.RepeatStart > MaxRepeatMs)
                {
                    s.RepeatTimer.Stop();
                    if (s.Down)
                    {
                        s.Down = false;  // 视为已松开；之后迟到的抬起会被忽略
                        Log.Write($"!! {key} 连发超过 {MaxRepeatMs / 1000:F0} 秒，自动停止（可能丢了「松开」）");
                    }
                    return;
                }
                s.RepeatTimer.Interval = repeatEvery;
                Fire(key, Gesture.Repeat);
            });
            s.RepeatTimer.Interval = repeatDelay;
            s.RepeatTimer.Start();
        }

        if (needs.HasFlag(Needs.Long))
        {
            s.LongTimer ??= NewTimer(() =>
            {
                s.LongTimer.Stop();
                if (!s.Down) return;
                s.LongFired = true;
                Fire(key, Gesture.Long);
            });
            s.LongTimer.Interval = longWait;
            s.LongTimer.Start();
        }

        if (needs.HasFlag(Needs.Double) && s.WaitingSecond)
        {
            s.DoubleTimer.Stop();
            s.WaitingSecond = false;
            s.DoubleConsumed = true;
            Fire(key, Gesture.Double);
        }

        if (needs == Needs.None)
            Fire(key, Gesture.Tap);
    }

    public void OnUp(string key)
    {
        var s = Get(key);
        if (!s.Down)
            return;
        s.Down = false;
        var needs = Spec(key);

        if (needs.HasFlag(Needs.Hold))
            Fire(key, Gesture.HoldEnd);
        if (needs.HasFlag(Needs.Repeat))
            s.RepeatTimer.Stop();

        if (needs.HasFlag(Needs.Long))
        {
            s.LongTimer.Stop();
            if (s.LongFired)
                return;
        }

        if (needs.HasFlag(Needs.Double))
        {
            if (s.DoubleConsumed)
            {
                s.DoubleConsumed = false;
                return;
            }
            s.WaitingSecond = true;
            s.DoubleTimer ??= NewTimer(() =>
            {
                s.DoubleTimer.Stop();
                s.WaitingSecond = false;
                Fire(key, Gesture.Tap);
            });
            s.DoubleTimer.Interval = doubleWait;
            s.DoubleTimer.Start();
            return;
        }

        if (needs.HasFlag(Needs.Long))
            Fire(key, Gesture.Tap);
    }

    /// <summary>暂停时调用：清掉所有进行中的状态，按住类手势补发结束。</summary>
    public void Reset()
    {
        foreach (var kv in states)
        {
            var s = kv.Value;
            s.LongTimer?.Stop();
            s.DoubleTimer?.Stop();
            s.RepeatTimer?.Stop();
            if (s.Down && Spec(kv.Key).HasFlag(Needs.Hold))
                Fire(kv.Key, Gesture.HoldEnd);
            s.Down = s.LongFired = s.WaitingSecond = s.DoubleConsumed = false;
        }
    }

    Needs Spec(string key) => specs.TryGetValue(key, out var n) ? n : Needs.None;

    State Get(string key)
    {
        if (!states.TryGetValue(key, out var s))
            states[key] = s = new State();
        return s;
    }

    void Fire(string key, Gesture g) => Fired?.Invoke(key, g);

    static DispatcherTimer NewTimer(Action tick)
    {
        var t = new DispatcherTimer(DispatcherPriority.Send);
        t.Tick += (_, _) => tick();
        return t;
    }
}
