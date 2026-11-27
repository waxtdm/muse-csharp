using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Muse
{
    // 全局常量与共享状态（对应 py 里的模块级变量）
    static class App
    {
        public const string ShellVersion = "2026-11-25-1255";
        public const string Version = "1.0.6";   // C# 版自己的版本号，自动更新靠它比对
        public const string UpdateUrl = "https://raw.githubusercontent.com/waxtdm/muse-csharp/main/version.json";
        public const string UpdateSourceZip = "https://codeload.github.com/waxtdm/muse-csharp/zip/refs/heads/main";
        public const int CorePort = 17890;      // mihomo mixed-port
        public const int CtrlPort = 19090;      // mihomo external-controller
        public const long CoreExeSize = 61192704;
        public const long MmdbSize = 8434612;
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
        public static JObject NewNameObj(string name)
        {
            JObject o = new JObject();
            o["name"] = name;
            return o;
        }

        public static JObject NewPathObj(string path)
        {
            JObject o = new JObject();
            o["path"] = path;
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
    }
}
