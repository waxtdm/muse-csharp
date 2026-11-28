using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Muse
{
    // 全局常量与共享状态（对应 py 里的模块级变量）
    static class App
    {
        public const string ShellVersion = "2026-11-25-1255";
        public const string Version = "1.0.21";   // C# 版自己的版本号，自动更新靠它比对
        public const string UpdateUrl = "https://raw.githubusercontent.com/waxtdm/muse-csharp/main/version.json";
        public const string UpdateSourceZip = "https://codeload.github.com/waxtdm/muse-csharp/zip/refs/heads/main";
        public const int CorePort = 17890;      // mihomo mixed-port
        public const int CtrlPort = 19090;      // mihomo external-controller
        public const long CoreExeSize = 61192704;
        public const long MmdbSize = 8434612;
        public const long GeositeSize = 4357245;
        public const long GeoipDatSize = 16676025;
        public const int LockPort = 17999;      // 单例锁端口
        public const long CacheLimit = 1536L * 1024 * 1024; // 1.5GB

        public static string LastProxyDesc = "直连";
        public static string CoreState = "未启动";
        public static bool CoreInUse = false;
        public static bool ProxyBlocked = false;
        public static bool RefreshDone = false;
        public static bool? RefreshOk = null;

        public static string BaseDir()
        {
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        }

        public static string DataRoot()
        {
            return Path.Combine(BaseDir(), "Muse数据");
        }

        public static string CoreDir()
        {
            return Path.Combine(DataRoot(), "代理核心");
        }

        public static string SettingsPath()
        {
            return Path.Combine(DataRoot(), "代理设置.json");
        }

        public static string AccountsPath()
        {
            return Path.Combine(DataRoot(), "账号列表.json");
        }

        // ---- 设置（代理设置.json，与 py 版同名同键，可直接复用旧文件）----
        public static JObject LoadSettings()
        {
            try
            {
                string p = SettingsPath();
                if (File.Exists(p))
                {
                    JObject d = JObject.Parse(File.ReadAllText(p, Encoding.UTF8));
                    if (d != null) return d;
                }
            }
            catch { }
            return new JObject();
        }

        public static void SaveSettings(JObject data)
        {
            Directory.CreateDirectory(DataRoot());
            string tmp = SettingsPath() + ".tmp";
            File.WriteAllText(tmp, data.ToString(Formatting.None), new UTF8Encoding(false));
            if (File.Exists(SettingsPath())) File.Delete(SettingsPath());
            File.Move(tmp, SettingsPath());
        }

        public static bool GetBool(JObject s, string key, bool def)
        {
            JToken t;
            if (s.TryGetValue(key, out t) && t.Type == JTokenType.Boolean) return (bool)t;
            return def;
        }

        public static string GetStr(JObject s, string key, string def)
        {
            JToken t;
            if (s.TryGetValue(key, out t) && t.Type == JTokenType.String) return (string)t;
            return def;
        }

        // C#5 兼容的小对象构造（不用字典初始化器语法）
        public static JObject NewKVObj(string key, string value)
        {
            JObject o = new JObject();
            o[key] = value;
            return o;
        }

        public static JObject NewSubObj(string url)
        {
            JObject o = new JObject();
            o["name"] = "订阅1";
            o["url"] = url;
            return o;
        }

        public static void Set(JObject s, string key, object val)
        {
            s[key] = val == null ? null : JToken.FromObject(val);
        }

        public static List<JObject> GetSubUrls(JObject s)
        {
            var list = new List<JObject>();
            JToken t;
            if (s.TryGetValue("sub_urls", out t))
            {
                JArray arr = t as JArray;
                if (arr != null)
                {
                    foreach (JToken e in arr)
                    {
                        JObject o = e as JObject;
                        if (o != null) list.Add((JObject)o.DeepClone());
                    }
                }
            }
            return list;
        }

        // ---- 核心 secret：优先读现有 config.yaml 里的 secret 行（与 py 版共用同一份配置不用重下订阅）；
        // 没有配置时随机生成一个，写配置时由 ProxyCore 一并写入。----
        private static string _secret;
        public static string CoreSecret
        {
            get
            {
                if (_secret != null) return _secret;
                try
                {
                    string cfg = Path.Combine(CoreDir(), "config.yaml");
                    if (File.Exists(cfg))
                    {
                        foreach (string line in File.ReadLines(cfg))
                        {
                            string l = line.Trim();
                            if (l.StartsWith("secret:"))
                            {
                                string v = l.Substring(7).Trim().Trim('"', '\'');
                                if (v.Length > 0) { _secret = v; return _secret; }
                            }
                        }
                    }
                }
                catch { }
                _secret = Guid.NewGuid().ToString("N");
                return _secret;
            }
        }

        public static long DirSize(string p)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        public static string FmtRate(long n)
        {
            if (n >= 1024 * 1024) return Math.Round(n / 1024.0 / 1024.0, 1) + "MB/s";
            if (n >= 1024) return Math.Round(n / 1024.0) + "KB/s";
            return n + "B/s";
        }

        public static void LogException(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(DataRoot(), "异常日志.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + ex + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        // ---- 跨对话框共用的小样板（原来各对话框里各写了一份）----
        // .NET Framework 4.8 的 TextBox 没有 PlaceholderText，用 cue banner 实现
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void SetCue(TextBox tb, string text)
        {
            try { SendMessage(tb.Handle, 0x1501, (IntPtr)1, text); } catch { }
        }

        // TableLayoutPanel 加一行（单列对话框的通用写法）
        public static void AddRow(TableLayoutPanel t, ref int row, Control c, int gap)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Margin = new Padding(0, 0, 0, gap);
            t.Controls.Add(c, 0, row);
            row++;
        }

        // 一次性定时器：到点跑一次就停
        public static void OneShot(int ms, Action a)
        {
            var t = new Timer();
            t.Interval = ms;
            t.Tick += delegate { t.Stop(); a(); };
            t.Start();
        }
    }
}
