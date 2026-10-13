using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static G20SHub.Native.Win32;

namespace G20SHub.Input;

/// <summary>
/// 遥控器 ⏻ 电源键：System Control 接口（MI_02&amp;Col03，Generic Desktop 0x01 / 0x80）→ 按下 / 抬起。
/// Windows 不把这个接口交给 Raw Input（只给 Consumer / 键盘 / 厂商接口），所以用后台线程直接打开 HID 设备读报文：
/// 只读、共享方式（和 hidapi 一样），不影响系统、不需要管理员权限。接收器拔掉后每 3 秒重新找一次。
/// 报文用系统自带 hid.dll 按报告描述符解析，G20S Pro 是 Report ID 5：bit0 = 0x81 关机，bit1 = 0x82 睡眠。
/// 注意：这里只是「另外」收到按键，拦不住 Windows 自己的电源 / 睡眠按钮操作（要在电源选项里设为「不采取任何操作」）。
/// </summary>
sealed class SystemControl
{
    int held;

    /// <summary>usage、是否按下、时间戳（在后台线程上触发）。</summary>
    public event Action<int, bool, double> Changed;

    public void Start() => new Thread(Run) { IsBackground = true, Name = "G20S-Power" }.Start();

    void Run()
    {
        bool missingLogged = false;
        while (true)
        {
            var path = FindRemoteHid("&Col03");
            using var h = path == null ? null : CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == null || h.IsInvalid || !HidD_GetPreparsedData(h, out var pp))
            {
                if (!missingLogged)
                    Log.Write("⏻ 没连上遥控器的电源键接口（接收器插上后自动连接）");
                missingLogged = true;
                Thread.Sleep(3000);
                continue;
            }
            missingLogged = false;
            try
            {
                var caps = new byte[64];
                HidP_GetCaps(pp, caps);
                var buf = new byte[Math.Max(2, (int)BitConverter.ToUInt16(caps, 4))];  // InputReportByteLength
                Log.Write("⏻ 已连接遥控器电源键接口");
                while (ReadFile(h, buf, (uint)buf.Length, out uint read, IntPtr.Zero) && read > 0)
                    OnReport(pp, buf, (int)read);
            }
            finally
            {
                HidD_FreePreparsedData(pp);
            }
            Log.Write("⏻ 电源键接口断开，3 秒后重连");
            Thread.Sleep(3000);
        }
    }

    void OnReport(IntPtr pp, byte[] buf, int length)
    {
        double ts = Clock.Ms;
        var report = new byte[length];
        Array.Copy(buf, report, length);
        int usage = 0;
        var usages = new ushort[8];
        uint n = (uint)usages.Length;
        if (HidP_GetUsages(HidP_Input, 0x01, 0, usages, ref n, pp, report, (uint)length) == HIDP_STATUS_SUCCESS)
            for (int i = 0; i < n && usage == 0; i++)
                if (usages[i] is >= RemoteKeys.SysPowerDown and <= RemoteKeys.SysWakeUp)
                    usage = usages[i];
        Log.Write($"[Raw] 遥控器 System Control 报文 {BitConverter.ToString(report).Replace('-', ' ')} → {(usage == 0 ? "松开" : RemoteKeys.SystemName(usage))}");
        if (usage == held)
            return;
        if (held != 0)
            Changed?.Invoke(held, false, ts);
        held = usage;
        if (usage != 0)
            Changed?.Invoke(usage, true, ts);
    }

    /// <summary>找遥控器的某个 HID 接口路径（设备路径里含 VID 和集合名，如 "&amp;Col03"）。</summary>
    static string FindRemoteHid(string collection)
    {
        HidD_GetHidGuid(out var guid);
        if (CM_Get_Device_Interface_List_Size(out uint len, ref guid, null, 0) != 0 || len <= 1)
            return null;
        var buf = new char[len];
        if (CM_Get_Device_Interface_List(ref guid, null, buf, len, 0) != 0)
            return null;
        var vid = $"VID_{RemoteKeys.RemoteVendorId:X4}";
        foreach (var p in new string(buf).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            if (p.IndexOf(vid, StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf(collection, StringComparison.OrdinalIgnoreCase) >= 0)
                return p;
        return null;
    }
}
