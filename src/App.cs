using System;
using System.IO;
using System.Threading;
using System.Windows;
using G20SHub.Actions;
using G20SHub.Gestures;
using G20SHub.Input;
using G20SHub.UI;

namespace G20SHub;

static class Program
{
    const string MutexName = @"Local\G20SHub", QuitEventName = @"Local\G20SHub.Quit";

    [STAThread]
    static int Main(string[] args)
    {
        int preview = Array.IndexOf(args, "--preview");
        if (preview >= 0 && preview + 1 < args.Length)
        {
            Preview.Run(args[preview + 1]);  // 自检：把界面画成图片后退出
            return 0;
        }

        // 只运行一个实例。已有旧实例时请它退出、由新版本接管（编译新版本后重新运行即可生效）
        using var mutex = new Mutex(false, MutexName);
        if (!Acquire(mutex, 0))
        {
            using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName))
                quit.Set();
            if (!Acquire(mutex, 6000))
            {
                MessageBox.Show("旧的 G20S Hub 没有退出。请在任务管理器里结束 G20SHub.exe 后再运行。", "G20S Hub");
                return 1;
            }
        }

        try
        {
            return Run(args);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    static bool Acquire(Mutex m, int timeoutMs)
    {
        try { return m.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { return true; }  // 旧实例被强制结束过
    }

    static int Run(string[] args)
    {
        bool dryRun = Array.IndexOf(args, "--dry-run") >= 0;
        AppPaths.Init(args);
        Log.Init(AppPaths.LogFile);
        KeySender.DryRun = dryRun;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("!! 界面线程异常: " + e.Exception);
            e.Handled = true;
        };

        var cfg = Config.Load(AppPaths.Config);
        var input = new InputThread(cfg, dryRun) { RestartExe = AppPaths.Exe };  // 🔇 / 托盘「重启」
        bool restarted = Array.IndexOf(args, "--restarted") >= 0;
        var hub = new Hub(cfg, AppPaths.DataDir);
        var engine = new GestureEngine(cfg, hub.Specs);
        var log = new DiagWindow(input, dryRun);
        Tray tray = null;

        engine.Fired += hub.OnGesture;
        hub.Voice.LatchChanged += latched => input.VoiceLatched = latched;
        hub.Ring.ModalChanged += open => input.Keys.RingModal = open;
        input.LatchedKey += (vk, scan, ext) =>
            app.Dispatcher.BeginInvoke(new Action(() => hub.OnKeyWhileLatched(vk, scan, ext)));
        // 万一崩溃，也要先松开可能按住的 Ctrl+Win
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            try { hub.ReleaseAll("程序异常"); } catch { }
        };

        // 输入线程 → 界面线程：按到达顺序投递，手势与动作都在界面线程上处理
        input.Event += ev => app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ev.Kind == InputKind.Paused)
            {
                if (ev.Down)
                {
                    engine.Reset();
                    hub.ReleaseAll("暂停");
                }
                tray?.SetPaused(ev.Down);
                Osd.Show(ev.Down ? "⏸ G20S 已暂停" : "▶ G20S 已恢复");
                return;
            }
            if (input.Paused)
                return;  // 暂停中：遥控器只走系统默认，不触发任何动作
            var key = Hub.KeyOf(ev);
            if (key == null)
                return;
            if (ev.Down) engine.OnDown(key);
            else engine.OnUp(key);
        }));

        Log.Write($"===== G20S Hub 启动 · 版本 {BuildInfo.Label}{(dryRun ? "（dry-run）" : "")} =====");
        Log.Write($"数据目录（{AppPaths.Mode}）：{AppPaths.DataDir}");
        try
        {
            input.Start();
        }
        catch (Exception e)
        {
            Log.Write("!! 启动失败: " + e.Message);
            MessageBox.Show("启动失败：" + e.Message, "G20S Hub");
            Log.Close();
            return 2;
        }

        void Exit()
        {
            DiagWindow.Exiting = true;
            app.Shutdown();
        }

        // 新版本启动时会触发这个事件：本实例退出，让位给新版本
        var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        new Thread(() =>
        {
            quitEvent.WaitOne();
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                Log.Write("新版本已启动，本实例退出");
                Exit();
            }));
        }) { IsBackground = true, Name = "G20S-Takeover" }.Start();

        app.Startup += (_, _) =>
        {
            tray = new Tray(BuildInfo.Label, hub.Ring.OpenSettings, () => { log.Show(); log.Activate(); }, input.TogglePause,
                () => input.RequestPanic("托盘"), Exit);
            Osd.Show(restarted ? $"🔄 G20S Hub 已重启 · 版本 {BuildInfo.Label}" : $"G20S Hub 已启动 · 版本 {BuildInfo.Label}");
        };
        app.Exit += (_, _) =>
        {
            hub.ReleaseAll("退出");
            input.Stop();
            tray?.Dispose();
            Log.Write("===== 退出 =====");
            Log.Close();
        };
        return app.Run();
    }
}
