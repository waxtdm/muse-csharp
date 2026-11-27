using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace Muse
{
    // 代理核心（mihomo）生命周期 + 订阅 + 探测（对应 py 的 ensure_core / start_core / core_api /
    // download_subscriptions / probe_works / auto_proxy_url 等）
    static class ProxyCore
    {
        static Process _coreProc;
        static readonly object StartLock = new object();

        // ---- 端口 / 注册表 / 连通性 ----
        public static bool PortOpen(int port, int timeoutMs = 250)
        {
            try
            {
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect("127.0.0.1", port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs);
                    if (!ok) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        static string ReadRegStr(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null) return "";
                    object v = k.GetValue(name);
                    return v == null ? "" : v.ToString();
                }
            }
            catch { return ""; }
        }

        static int ReadRegInt(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null) return 0;
                    object v = k.GetValue(name);
                    if (v is int) return (int)v;
                    int n;
                    if (v != null && int.TryParse(v.ToString(), out n)) return n;
                    return 0;
                }
            }
            catch { return 0; }
        }

        static string PickProxyServer(string server)
        {
            if (string.IsNullOrEmpty(server)) return "";
            if (server.Contains("="))
            {
                var parts = new Dictionary<string, string>();
                foreach (string p in server.Split(';'))
                {
                    int i = p.IndexOf('=');
                    if (i > 0) parts[p.Substring(0, i)] = p.Substring(i + 1);
                }
                string v;
                if (parts.TryGetValue("https", out v) && v.Length > 0) server = v;
                else if (parts.TryGetValue("http", out v) && v.Length > 0) server = v;
                else return "";
            }
            server = server.Trim();
            if (server.Length == 0) return "";
            return server.Contains("://") ? server : "http://" + server;
        }

        static readonly int[] LocalPorts = new int[] { 7890, 7897, 10809, 10808, 20171, 1080, 8080, 8888 };

        public static string ProbeLocalPort()
        {
            var tasks = new List<Task<Tuple<int, bool>>>();
            foreach (int p in LocalPorts)
            {
                int port = p;
                tasks.Add(Task.Run(delegate () { return Tuple.Create(port, PortOpen(port)); }));
            }
            Task.WaitAll(tasks.ToArray());
            foreach (Task<Tuple<int, bool>> t in tasks)
            {
                if (t.Result.Item2) return "http://127.0.0.1:" + t.Result.Item1;
            }
            return "";
        }

        public static string ReadSystemProxy()
        {
            if (ReadRegInt("ProxyEnable") == 0) return "";
            return PickProxyServer(ReadRegStr("ProxyServer"));
        }

        public static string ReadPacUrl()
        {
            return ReadRegStr("AutoConfigURL");
        }

        // 系统代理优先，否则扫本机常用端口（与 py 的 auto_proxy_url 一致）
        public static string AutoProxyUrl()
        {
            string s = ReadSystemProxy();
            if (s.Length > 0) return s;
            return ProbeLocalPort();
        }

        // 通过指定代理（空串=直连）GET https://muse.ai，状态码 <500 算通
        public static bool ProbeWorks(string proxyUrl)
        {
            try
            {
                var h = new HttpClientHandler();
                if (string.IsNullOrEmpty(proxyUrl)) h.UseProxy = false;
                else { h.UseProxy = true; h.Proxy = new WebProxy(proxyUrl); }
                using (var c = new HttpClient(h))
                {
                    c.Timeout = TimeSpan.FromSeconds(4);
                    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                    HttpResponseMessage r = c.GetAsync("https://muse.ai").GetAwaiter().GetResult();
                    return (int)r.StatusCode < 500;
                }
            }
            catch { return false; }
        }

        // ---- 核心文件 ----
        public static string FindCoreExe()
        {
            foreach (string cand in new string[]
            {
                Path.Combine(App.BaseDir(), "mihomo.exe"),
                Path.Combine(App.CoreDir(), "mihomo.exe"),
            })
            {
                if (File.Exists(cand)) return cand;
            }
            return null;
        }

        // 内嵌资源优先复用已有文件：程序旁或 Muse数据/代理核心/ 下已有且大小对得上就直接用；
        // 没有就从 exe 内嵌的资源里释放出来（和 py 版内嵌 base64 一个意思），别人拿到就能直接跑。
        public static string EnsureCore()
        {
            string exe = FindCoreExe();
            if (exe != null)
            {
                try
                {
                    if (Path.GetDirectoryName(exe) == App.CoreDir()
                        && new FileInfo(exe).Length != App.CoreExeSize) exe = null;
                }
                catch { exe = null; }
            }
            if (exe != null) return exe;
            try
            {
                using (System.IO.Stream rs = typeof(App).Assembly.GetManifestResourceStream("Muse.mihomo.exe"))
                {
                    if (rs == null) return null;
                    Directory.CreateDirectory(App.CoreDir());
                    string target = Path.Combine(App.CoreDir(), "mihomo.exe");
                    using (FileStream f = new FileStream(target, FileMode.Create, FileAccess.Write))
                    {
                        rs.CopyTo(f);
                    }
                    return target;
                }
            }
            catch { return null; }
        }

        public static bool EnsureMmdb()
        {
            try
            {
                string target = Path.Combine(App.CoreDir(), "geoip.metadb");
                if (File.Exists(target) && new FileInfo(target).Length == App.MmdbSize) return true;
                using (System.IO.Stream rs = typeof(App).Assembly.GetManifestResourceStream("Muse.geoip.metadb"))
                {
                    if (rs == null) return false;
                    Directory.CreateDirectory(App.CoreDir());
                    using (FileStream f = new FileStream(target, FileMode.Create, FileAccess.Write))
                    {
                        rs.CopyTo(f);
                    }
                    return new FileInfo(target).Length == App.MmdbSize;
                }
            }
            catch { return false; }
        }

        // 地理数据：GEOSITE 规则要 geosite.dat，geodata-mode 开时 GEOIP 要 geoip.dat。
        // 以前只带了 geoip.metadb，订阅里一旦有 GEOSITE 规则，核心启动/校验时就得去 GitHub 现下载，
        // 国内网络慢就卡住——订阅本身没问题，是工具少带了文件。
        public static bool EnsureGeodata()
        {
            bool ok = true;
            ok = EnsureOneGeodata("geosite.dat", App.GeositeSize) && ok;
            ok = EnsureOneGeodata("geoip.dat", App.GeoipDatSize) && ok;
            return ok;
        }

        static bool EnsureOneGeodata(string name, long size)
        {
            try
            {
                string target = Path.Combine(App.CoreDir(), name);
                if (File.Exists(target) && new FileInfo(target).Length == size) return true;
                using (System.IO.Stream rs = typeof(App).Assembly.GetManifestResourceStream("Muse." + name))
                {
                    if (rs == null) return false;
                    Directory.CreateDirectory(App.CoreDir());
                    using (FileStream f = new FileStream(target, FileMode.Create, FileAccess.Write))
                    {
                        rs.CopyTo(f);
                    }
                    return new FileInfo(target).Length == size;
                }
            }
            catch { return false; }
        }

        // ---- 核心 HTTP API（http://127.0.0.1:19090，Bearer 鉴权）----
        public static JToken CoreApi(string path, string method = "GET", JToken payload = null)
        {
            using (var c = new HttpClient())
            {
                c.Timeout = TimeSpan.FromSeconds(8);
                c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + App.CoreSecret);
                string url = "http://127.0.0.1:" + App.CtrlPort + path;
                HttpResponseMessage resp;
                if (method == "GET")
                {
                    resp = c.GetAsync(url).GetAwaiter().GetResult();
                }
                else
                {
                    string body = payload == null ? "" : payload.ToString(Newtonsoft.Json.Formatting.None);
                    var content = new StringContent(body, Encoding.UTF8, "application/json");
                    if (method == "PUT") resp = c.PutAsync(url, content).GetAwaiter().GetResult();
                    else if (method == "POST") resp = c.PostAsync(url, content).GetAwaiter().GetResult();
                    else resp = c.GetAsync(url).GetAwaiter().GetResult();
                }
                string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(text)) return new JObject();
                return JToken.Parse(text);
            }
        }

        public static bool CoreAlive()
        {
            try { CoreApi("/version"); return true; }
            catch { return false; }
        }

        public static string CoreLogTail()
        {
            try
            {
                string p = Path.Combine(App.CoreDir(), "core.log");
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long size = fs.Length;
                    long start = Math.Max(0, size - 800);
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[size - start];
                    fs.Read(buf, 0, buf.Length);
                    string text = Encoding.UTF8.GetString(buf).Trim();
                    if (text.Length == 0) return "日志是空的（核心可能被系统或杀毒拦了，根本没跑起来）";
                    return text.Length <= 350 ? text : text.Substring(text.Length - 350);
                }
            }
            catch { return "读不到 core.log"; }
        }

        public static void KillLeftoverCore()
        {
            try
            {
                string d = App.CoreDir().Replace("'", "''");
                string ps = "Get-CimInstance Win32_Process -Filter \"Name='mihomo.exe'\" | "
                    + "Where-Object { $_.CommandLine -like '*" + d + "*' } | "
                    + "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
                var psi = new ProcessStartInfo("powershell",
                    "-NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"");
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi)) { p.WaitForExit(10000); }
                Thread.Sleep(1000);
            }
            catch { }
        }

        // 启动前把 config.yaml 里的端口/secret 行改成程序用的值（与 py 一致）
        static void FixConfigPorts()
        {
            try
            {
                string cfg = Path.Combine(App.CoreDir(), "config.yaml");
                if (!File.Exists(cfg)) return;
                string text = File.ReadAllText(cfg, Encoding.UTF8);
                text = Regex.Replace(text, "(?m)^mixed-port:.*$", "mixed-port: " + App.CorePort);
                text = Regex.Replace(text, "(?m)^external-controller:.*$",
                    "external-controller: 127.0.0.1:" + App.CtrlPort);
                text = Regex.Replace(text, "(?m)^secret:.*$", "secret: " + App.CoreSecret);
                if (!text.Contains("external-controller:"))
                    text = "external-controller: 127.0.0.1:" + App.CtrlPort + "\r\n" + text;
                if (!text.Contains("secret:"))
                    text = "secret: " + App.CoreSecret + "\r\n" + text;
                File.WriteAllText(cfg, text, new UTF8Encoding(false));
            }
            catch { }
        }

        static bool SpawnOnce(int waitS)
        {
            try
            {
                string lp = Path.Combine(App.CoreDir(), "core.log");
                if (File.Exists(lp) && new FileInfo(lp).Length > 20 * 1024 * 1024)
                {
                    try { File.Move(lp, lp + ".old"); } catch { }
                }
            }
            catch { }
            string exe = EnsureCore();
            if (exe == null) { App.CoreState = "核心释放失败"; return false; }
            string cfg = Path.Combine(App.CoreDir(), "config.yaml");
            var psi = new ProcessStartInfo(exe, "-d \"" + App.CoreDir() + "\" -f \"" + cfg + "\"");
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.UseShellExecute = false;
            try
            {
                var fs = new FileStream(Path.Combine(App.CoreDir(), "core.log"),
                    FileMode.Append, FileAccess.Write, FileShare.Read);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                _coreProc = Process.Start(psi);
                _coreProc.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { try { fs.Write(Encoding.UTF8.GetBytes(e.Data + "\r\n"), 0, Encoding.UTF8.GetByteCount(e.Data + "\r\n")); fs.Flush(); } catch { } }
                };
                _coreProc.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { try { fs.Write(Encoding.UTF8.GetBytes(e.Data + "\r\n"), 0, Encoding.UTF8.GetByteCount(e.Data + "\r\n")); fs.Flush(); } catch { } }
                };
                _coreProc.BeginOutputReadLine();
                _coreProc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                App.CoreState = "启动失败：" + ex.Message;
                return false;
            }
            for (int i = 0; i < Math.Max(1, waitS * 2); i++)
            {
                if (PortOpen(App.CorePort, 200) && CoreAlive())
                {
                    App.CoreState = "运行中";
                    return true;
                }
                if (_coreProc.HasExited)
                {
                    App.CoreState = "启动失败（退出码 " + _coreProc.ExitCode + "）：" + CoreLogTail();
                    return false;
                }
                Thread.Sleep(500);
            }
            App.CoreState = "启动超时：" + CoreLogTail();
            return false;
        }

        static bool StartCoreImpl(int waitS)
        {
            if (CoreAlive()) { App.CoreState = "运行中（已接管）"; return true; }
            if (_coreProc != null)
            {
                if (!_coreProc.HasExited)
                {
                    for (int i = 0; i < Math.Max(1, waitS * 2); i++)
                    {
                        if (CoreAlive()) { App.CoreState = "运行中"; return true; }
                        if (_coreProc.HasExited) break;
                        Thread.Sleep(500);
                    }
                    if (CoreAlive()) { App.CoreState = "运行中"; return true; }
                }
                _coreProc = null;
            }
            if (PortOpen(App.CorePort) || PortOpen(App.CtrlPort)) KillLeftoverCore();
            string exe = EnsureCore();
            if (exe == null) { App.CoreState = "核心文件没找到"; return false; }
            string cfg = Path.Combine(App.CoreDir(), "config.yaml");
            if (!File.Exists(cfg)) { App.CoreState = "还没有订阅配置"; return false; }
            FixConfigPorts();
            EnsureMmdb();
            EnsureGeodata();
            if (SpawnOnce(waitS)) return true;
            bool exited = _coreProc != null && _coreProc.HasExited;
            if (!exited && _coreProc != null)
            {
                for (int i = 0; i < Math.Max(1, waitS * 2); i++)
                {
                    if (CoreAlive()) { App.CoreState = "运行中"; return true; }
                    if (_coreProc.HasExited) { exited = true; break; }
                    Thread.Sleep(500);
                }
                if (CoreAlive()) { App.CoreState = "运行中"; return true; }
            }
            KillLeftoverCore();
            if (exited)
            {
                try
                {
                    string cacheDb = Path.Combine(App.CoreDir(), "cache.db");
                    if (File.Exists(cacheDb)) File.Delete(cacheDb);
                }
                catch { }
            }
            EnsureMmdb();
            EnsureGeodata();
            _coreProc = null;
            return SpawnOnce(waitS);
        }

        public static bool StartCore(int waitS = 60)
        {
            lock (StartLock) { return StartCoreImpl(waitS); }
        }

        public static void StopCore()
        {
            try
            {
                if (_coreProc != null && !_coreProc.HasExited) _coreProc.Kill();
            }
            catch { }
            _coreProc = null;
        }

        // ---- 订阅 ----
        static Tuple<Dictionary<object, object>, string> FetchSubCfg(string url)
        {
            string text = null;
            Exception lastErr = null;
            foreach (string proxy in new string[] { AutoProxyUrl(), "" })
            {
                try
                {
                    var h = new HttpClientHandler();
                    if (string.IsNullOrEmpty(proxy)) h.UseProxy = false;
                    else { h.UseProxy = true; h.Proxy = new WebProxy(proxy); }
                    using (var c = new HttpClient(h))
                    {
                        c.Timeout = TimeSpan.FromSeconds(15);
                        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "clash.meta");
                        text = c.GetStringAsync(url).GetAwaiter().GetResult();
                    }
                    break;
                }
                catch (Exception ex) { lastErr = ex; }
            }
            if (text == null) return Tuple.Create<Dictionary<object, object>, string>(null, "下载失败：" + ShortErr(lastErr));
            try
            {
                var cfg = new DeserializerBuilder().Build()
                    .Deserialize<Dictionary<object, object>>(text);
                if (cfg == null || !cfg.ContainsKey("proxies"))
                    return Tuple.Create<Dictionary<object, object>, string>(null, "里面没找到节点，可能不是 Clash 格式订阅");
                return Tuple.Create(cfg, "");
            }
            catch (Exception ex)
            {
                return Tuple.Create<Dictionary<object, object>, string>(null, "解析失败：" + ex.Message);
            }
        }

        // 报错只取最里层异常的一句话，别把整段堆栈贴给用户看
        static string ShortErr(Exception ex)
        {
            if (ex == null) return "未知错误";
            Exception e = ex;
            while (e.InnerException != null) e = e.InnerException;
            string m = e.Message ?? "";
            if (m.Length > 120) m = m.Substring(0, 120) + "…";
            return m;
        }

        static string NodeKey(Dictionary<object, object> p)
        {
            object t, s, port, pw, uuid;
            p.TryGetValue("type", out t);
            p.TryGetValue("server", out s);
            p.TryGetValue("port", out port);
            p.TryGetValue("password", out pw);
            p.TryGetValue("uuid", out uuid);
            string secret = pw != null ? pw.ToString() : (uuid != null ? uuid.ToString() : "");
            return (t ?? "") + "|" + (s ?? "") + "|" + (port ?? "") + "|" + secret;
        }

        static List<Dictionary<object, object>> GetProxies(Dictionary<object, object> cfg)
        {
            var list = new List<Dictionary<object, object>>();
            object v;
            List<object> arr = null;
            if (cfg.TryGetValue("proxies", out v)) arr = v as List<object>;
            if (arr != null)
            {
                foreach (object e in arr)
                {
                    var d = e as Dictionary<object, object>;
                    if (d != null) list.Add(d);
                }
            }
            return list;
        }

        // 上次订阅校验超时的配置留存路径（超时时才有值，供界面问用户要不要跳过校验直接启用）
        public static string LastTimeoutConfig = null;

        // 把超时留存的配置正式启用：先备份旧配置，再搬过去
        public static bool InstallKeptConfig()
        {
            try
            {
                string src = LastTimeoutConfig;
                if (string.IsNullOrEmpty(src) || !File.Exists(src)) return false;
                string dest = Path.Combine(App.CoreDir(), "config.yaml");
                if (File.Exists(dest)) File.Copy(dest, dest + ".bak", true);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(src, dest);
                LastTimeoutConfig = null;
                return true;
            }
            catch { return false; }
        }

        static Tuple<bool, string> DownloadSubscriptionsImpl(List<string> urls)
        {
            LastTimeoutConfig = null;
            var cfgs = new List<Dictionary<object, object>>();
            var errors = new List<string>();
            foreach (string u in urls)
            {
                Tuple<Dictionary<object, object>, string> r = FetchSubCfg(u);
                if (r.Item1 == null) errors.Add(r.Item2);
                else cfgs.Add(r.Item1);
            }
            if (cfgs.Count == 0) return Tuple.Create(false, "订阅全部失败：" + string.Join("；", errors));
            Dictionary<object, object> bas = cfgs[0];
            List<Dictionary<object, object>> proxies = GetProxies(bas);
            var seen = new HashSet<string>();
            foreach (Dictionary<object, object> p in proxies) seen.Add(NodeKey(p));
            var used = new HashSet<string>();
            foreach (Dictionary<object, object> p in proxies)
            {
                object n;
                if (p.TryGetValue("name", out n) && n != null) used.Add(n.ToString());
            }
            var added = new List<string>();
            int dup = 0;
            for (int ci = 1; ci < cfgs.Count; ci++)
            {
                foreach (Dictionary<object, object> p in GetProxies(cfgs[ci]))
                {
                    object nobj;
                    if (!p.TryGetValue("name", out nobj) || nobj == null) continue;
                    string name = nobj.ToString();
                    string k = NodeKey(p);
                    if (seen.Contains(k)) { dup++; continue; }
                    seen.Add(k);
                    string nw = name;
                    int i = 2;
                    while (used.Contains(nw)) { nw = name + " (" + i + ")"; i++; }
                    if (nw != name) p["name"] = nw;
                    used.Add(nw);
                    proxies.Add(p);
                    added.Add(nw);
                }
            }
            bas["proxies"] = proxies.Cast<object>().ToList();
            if (added.Count > 0)
            {
                object gv;
                List<object> groups = null;
                if (bas.TryGetValue("proxy-groups", out gv)) groups = gv as List<object>;
                if (groups != null)
                {
                    foreach (object g in groups)
                    {
                        var gd = g as Dictionary<object, object>;
                        if (gd == null) continue;
                        object gt;
                        gd.TryGetValue("type", out gt);
                        string type = gt == null ? "" : gt.ToString();
                        if ((type == "select" || type == "url-test" || type == "fallback")
                            && !gd.ContainsKey("filter"))
                        {
                            object gp;
                            List<object> gl;
                            if (!gd.TryGetValue("proxies", out gp) || !(gp is List<object>))
                            {
                                gl = new List<object>();
                                gd["proxies"] = gl;
                            }
                            else gl = (List<object>)gp;
                            foreach (string n in added)
                            {
                                if (!gl.Contains(n)) gl.Add(n);
                            }
                        }
                    }
                }
            }
            bas["mixed-port"] = App.CorePort;
            bas["external-controller"] = "127.0.0.1:" + App.CtrlPort;
            bas["secret"] = App.CoreSecret;
            bas["allow-lan"] = false;
            Directory.CreateDirectory(App.CoreDir());
            string tmp = Path.Combine(App.CoreDir(), "config.yaml." + Process.GetCurrentProcess().Id + ".tmp");
            string yaml = new SerializerBuilder().DisableAliases().Build().Serialize(bas);
            File.WriteAllText(tmp, yaml, new UTF8Encoding(false));
            try
            {
                string exe = EnsureCore();
                EnsureMmdb();
                EnsureGeodata();
                if (exe != null)
                {
                    var psi = new ProcessStartInfo(exe,
                        "-t -d \"" + App.CoreDir() + "\" -f \"" + tmp + "\"");
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    psi.UseShellExecute = false;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    // 校验输出要收下来：以前直接丢掉，用户只能看到“没通过”，不知道为什么没通过
                    var outSb = new StringBuilder();
                    var errSb = new StringBuilder();
                    using (Process p = Process.Start(psi))
                    {
                        p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) outSb.AppendLine(e.Data); };
                        p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) errSb.AppendLine(e.Data); };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(60000))
                        {
                            try { p.Kill(); } catch { }
                            try { p.WaitForExit(3000); } catch { }
                            // 超时也要留证据：当时吐到一半的输出 + 失败的配置都存下来，不然下次还是两眼一抹黑
                            string partial = errSb.ToString().Trim();
                            if (partial.Length == 0) partial = outSb.ToString().Trim();
                            try { File.WriteAllText(Path.Combine(App.CoreDir(), "订阅校验.log"), "mihomo -t 校验超时（60 秒没出结果，已杀掉进程）\r\n\r\n当时已输出的内容：\r\n" + partial, new UTF8Encoding(false)); } catch { }
                            string bad = Path.Combine(App.CoreDir(), "config.yaml.校验失败.yaml");
                            try { if (File.Exists(bad)) File.Delete(bad); File.Move(tmp, bad); } catch { try { File.Delete(tmp); } catch { } }
                            LastTimeoutConfig = File.Exists(bad) ? bad : null;
                            string tail = partial;
                            if (tail.Length > 400) tail = tail.Substring(tail.Length - 400);
                            string m = "新配置校验超时（60 秒没出结果），已保留旧配置。";
                            if (tail.Length > 0) m += "当时核心已输出：" + tail;
                            m += "\r\n完整输出见 代理核心/订阅校验.log，失败的配置已存为 config.yaml.校验失败.yaml";
                            return Tuple.Create(false, m);
                        }
                        p.WaitForExit();
                        if (p.ExitCode != 0)
                        {
                            string detail = errSb.ToString().Trim();
                            if (detail.Length == 0) detail = outSb.ToString().Trim();
                            // 详情写日志，失败的配置留一份，方便对照查
                            try { File.WriteAllText(Path.Combine(App.CoreDir(), "订阅校验.log"), "mihomo -t 退出码 " + p.ExitCode + "\r\n\r\n" + detail, new UTF8Encoding(false)); } catch { }
                            string bad = Path.Combine(App.CoreDir(), "config.yaml.校验失败.yaml");
                            try { if (File.Exists(bad)) File.Delete(bad); File.Move(tmp, bad); } catch { try { File.Delete(tmp); } catch { } }
                            string shortErr = detail;
                            if (shortErr.Length > 400) shortErr = shortErr.Substring(shortErr.Length - 400);
                            return Tuple.Create(false, "新配置没通过核心校验，已保留旧配置。原因：" + shortErr + "\r\n完整输出见 代理核心/订阅校验.log，失败的配置已存为 config.yaml.校验失败.yaml");
                        }
                    }
                }
            }
            catch { }
            string dest = Path.Combine(App.CoreDir(), "config.yaml");
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            string msg = "已合并 " + cfgs.Count + " 个订阅，共 " + proxies.Count + " 个节点";
            if (dup > 0) msg += "；跳过重复节点 " + dup + " 个";
            if (errors.Count > 0) msg += "；另有 " + errors.Count + " 个订阅失败：" + string.Join("；", errors);
            return Tuple.Create(true, msg);
        }

        public static Tuple<bool, string> DownloadSubscriptions(List<string> urls)
        {
            try { return DownloadSubscriptionsImpl(urls); }
            catch (Exception ex) { return Tuple.Create(false, "处理订阅出错：" + ex.Message); }
        }

        // 配置超过 24 小时，后台自动刷新一次
        public static void MaybeRefreshSubscription(JObject settings)
        {
            var urls = new List<string>();
            foreach (JObject e in App.GetSubUrls(settings))
            {
                string u = (string)e["url"];
                if (!string.IsNullOrEmpty(u)) urls.Add(u);
            }
            if (urls.Count == 0) return;
            string cfg = Path.Combine(App.CoreDir(), "config.yaml");
            bool stale = true;
            if (File.Exists(cfg)) stale = (DateTime.Now - File.GetLastWriteTime(cfg)).TotalSeconds > 24 * 3600;
            if (!stale) return;
            Task.Run(delegate ()
            {
                Tuple<bool, string> r = DownloadSubscriptions(urls);
                App.RefreshDone = true;
                App.RefreshOk = r.Item1;
                if (r.Item1 && CoreAlive())
                {
                    try
                    {
                        CoreApi("/configs", "PUT", App.NewPathObj(cfg));
                    }
                    catch { }
                }
            });
        }
    }
}
