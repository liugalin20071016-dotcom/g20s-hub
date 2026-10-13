using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace G20SHub;

/// <summary>高精度单调时钟（毫秒）。</summary>
static class Clock
{
    static readonly double Factor = 1000.0 / Stopwatch.Frequency;
    public static double Ms => Stopwatch.GetTimestamp() * Factor;
}

/// <summary>异步写日志：调用方只入队，钩子线程里不做磁盘 IO。</summary>
static class Log
{
    static readonly BlockingCollection<string> Queue = new();
    static Thread writer;

    public static event Action<string> Line;

    const long MaxBytes = 4 << 20;

    public static void Init(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        // 超过 4MB 时把旧日志挪成 events.old.log，只保留一份
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                var old = Path.ChangeExtension(path, ".old.log");
                File.Delete(old);
                File.Move(path, old);
            }
        }
        catch { }
        var w =new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
        writer = new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
                w.WriteLine(line);
            w.Dispose();
        }) { IsBackground = true, Name = "G20S-Log" };
        writer.Start();
    }

    public static void Write(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {msg}";
        if (!Queue.IsAddingCompleted)
            Queue.Add(line);
        Line?.Invoke(line);
    }

    public static void Close()
    {
        Queue.CompleteAdding();
        writer?.Join(1000);
    }
}

/// <summary>config.json 读取。用 .NET Framework 自带的 JavaScriptSerializer。</summary>
sealed class Config
{
    readonly Dictionary<string, object> root;

    Config(Dictionary<string, object> root) => this.root = root;

    public static Config Load(string path)
    {
        if (!File.Exists(path))
        {
            Log.Write($"!! 找不到配置 {path}，使用默认值");
            return new Config(new Dictionary<string, object>());
        }
        var json = File.ReadAllText(path, Encoding.UTF8);
        var obj = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
        return new Config(obj ?? new Dictionary<string, object>());
    }

    /// <summary>按 "timing.match_ms" 这样的路径取数值。</summary>
    public double Num(string path, double fallback)
    {
        var v = Get(path);
        if (v == null) return fallback;
        try { return Convert.ToDouble(v); }
        catch { return fallback; }
    }

    public string Str(string path, string fallback) => Get(path) as string ?? fallback;

    public Dictionary<string, object> Section(string path) => Get(path) as Dictionary<string, object>;

    public string[] List(string path, string[] fallback)
    {
        if (Get(path) is not object[] arr) return fallback;
        var result = new string[arr.Length];
        for (int i = 0; i < arr.Length; i++) result[i] = Convert.ToString(arr[i]);
        return result;
    }

    object Get(string path)
    {
        object cur = root;
        foreach (var part in path.Split('.'))
        {
            if (cur is Dictionary<string, object> d && d.TryGetValue(part, out var next))
                cur = next;
            else
                return null;
        }
        return cur;
    }
}
