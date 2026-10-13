using System;
using System.Collections.Generic;
using G20SHub.Native;

namespace G20SHub.Input;

/// <summary>
/// 键盘接口的键无法区分来源（钩子先于 Raw Input），所以只在两种情况下接管：
/// - AppsKey（☰ 菜单）：全局接管（用户的真键盘没有菜单键）
/// - 圆环打开期间：方向键与 NumpadEnter（任何键盘）
/// 其余键一律原样放行。
/// </summary>
sealed class KeyboardKeys
{
    public const int Apps = 0x5D, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;
    public const int NumpadEnter = 0x10D;  // VK_RETURN + 扩展键标志

    readonly HashSet<int> modalDown = new();
    bool appsDown;

    public volatile bool RingModal;
    public bool DryRun;

    /// <summary>键码, 是否按下, 时间戳（输入线程上触发；按住时的系统连发已去重）。</summary>
    public event Action<int, bool, double> Key;

    public bool Decide(uint vk, uint flags, bool up, double now)
    {
        if (vk == Apps)
        {
            if (!up && !appsDown)
            {
                appsDown = true;
                Key?.Invoke(Apps, true, now);
            }
            else if (up)
            {
                appsDown = false;
                Key?.Invoke(Apps, false, now);
            }
            return !DryRun;
        }

        // 遥控器 OK 在 Raw Input 里是 Enter、在 Interception 里是 NumpadEnter，圆环模态下两种 Enter 都当 OK
        int code = vk == 0x0D ? NumpadEnter : (int)vk;
        bool ringKey = code is Left or Up or Right or Down or NumpadEnter;
        if (!ringKey || !(RingModal || modalDown.Contains(code)))
            return false;

        // 圆环打开时按下的键，其抬起也要吞掉（即使圆环已关闭）
        if (!up)
        {
            if (modalDown.Add(code))
                Key?.Invoke(code, true, now);
        }
        else
        {
            modalDown.Remove(code);
            Key?.Invoke(code, false, now);
        }
        return !DryRun;
    }
}
