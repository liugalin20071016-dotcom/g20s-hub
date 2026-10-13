using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace G20SHub.Ring;

enum Dir { Up, Right, Down, Left }

/// <summary>圆环的一个格子：要打开哪个程序的窗口，以及没在运行时怎么启动。</summary>
sealed class Slot
{
    public string Name = "";
    /// <summary>程序文件名，如 claude.exe（必填，用来匹配窗口）。</summary>
    public string Exe = "";
    /// <summary>程序完整路径，用于取图标。</summary>
    public string Path = "";
    /// <summary>窗口类名，留空则不限。</summary>
    public string Class = "";
    /// <summary>窗口标题需包含的文字，留空则该程序的任意窗口都算。</summary>
    public string TitleContains = "";
    /// <summary>窗口不存在时启动：exe / 快捷方式 / 网址 / shell:AppsFolder\应用ID。</summary>
    public string Launch = "";

    public Slot Clone() => (Slot)MemberwiseClone();
}

/// <summary>
/// 8 个格子：第 1 圈 ↑→↓←、第 2 圈 ↑→↓←。存放在项目根目录的 ring.json（由设置窗口写入）。
/// </summary>
sealed class RingSlots
{
    public static readonly string[] Keys =
    {
        "1-up", "1-right", "1-down", "1-left",
        "2-up", "2-right", "2-down", "2-left",
    };

    readonly string path;
    public Dictionary<string, Slot> Slots { get; set; } = new();

    public RingSlots(string path)
    {
        this.path = path;
        Load();
    }

    public static string KeyOf(int page, Dir dir) => $"{page}-{dir.ToString().ToLowerInvariant()}";

    public Slot Get(int page, Dir dir) => Slots.TryGetValue(KeyOf(page, dir), out var s) ? s : null;

    public bool IsEmpty => Slots.Count == 0;

    public void Load()
    {
        Slots = new Dictionary<string, Slot>();
        if (!File.Exists(path))
            return;
        try
        {
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null)
                return;
            foreach (var key in Keys)
            {
                if (!root.TryGetValue(key, out var v) || v is not Dictionary<string, object> d)
                    continue;
                string S(string k) => d.TryGetValue(k, out var x) ? Convert.ToString(x) ?? "" : "";
                var slot = new Slot
                {
                    Name = S("name"), Exe = S("exe"), Path = S("path"), Class = S("class"),
                    TitleContains = S("title_contains"), Launch = S("launch"),
                };
                if (slot.Exe.Length > 0)
                    Slots[key] = slot;
            }
        }
        catch (Exception e)
        {
            Log.Write($"!! 读取 {System.IO.Path.GetFileName(path)} 失败：{e.Message}");
        }
    }

    public void Save(Dictionary<string, Slot> slots)
    {
        var js = new JavaScriptSerializer();
        var lines = Keys.Where(slots.ContainsKey).Select(k =>
        {
            var s = slots[k];
            var d = new Dictionary<string, string>
            {
                ["name"] = s.Name, ["exe"] = s.Exe, ["path"] = s.Path, ["class"] = s.Class,
                ["title_contains"] = s.TitleContains, ["launch"] = s.Launch,
            };
            return $"  \"{k}\": {js.Serialize(d)}";
        });
        File.WriteAllText(path, "{\n" + string.Join(",\n", lines) + "\n}\n", new UTF8Encoding(false));
        Slots = slots.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        Log.Write($"圆环格子已保存：{Slots.Count} 个");
    }
}
