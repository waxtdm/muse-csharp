using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using System.Net;

namespace Muse
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // 订阅服务器只认 TLS 1.2+，老 .NET 默认只用 TLS 1.0，握手会报“未能创建 SSL/TLS 安全通道”
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls; }
            catch { try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)0xC00; } catch { } }
            // 上次自动更新重启后，顺手清掉临时目录和更新器残留（各版本独立目录，全清，单个清不掉就跳过）
            try
            {
                foreach (string d in Directory.GetDirectories(Path.GetTempPath(), "MuseUpdate*"))
                {
                    try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
            try
            {
                string ub = Path.Combine(UpdateCheck.AppRoot(), "update.bat");
                if (File.Exists(ub)) File.Delete(ub);
            }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) App.LogException(ex);
            };

            // 单例：占住 127.0.0.1:17999（与 py 版同一机制）
            TcpListener locker = null;
            try
            {
                locker = new TcpListener(IPAddress.Loopback, App.LockPort);
                locker.Start();
            }
            catch
            {
                MessageBox.Show("Muse 已经在运行了，不用重复打开。", "Muse",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            JObject settings = App.LoadSettings();
            if (settings["sub_urls"] == null)
            {
                string u = App.GetStr(settings, "sub_url", "");
                var arr = new JArray();
                if (u.Length > 0) arr.Add(App.NewSubObj(u));
                settings["sub_urls"] = arr;
            }

            // 版本变了：清掉上次的路由，删各账号缓存、核心 cache.db（与 py 一致）
            try
            {
                string marker = Path.Combine(App.DataRoot(), "版本标记.txt");
                string old = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
                if (old != App.ShellVersion)
                {
                    settings.Remove("last_flag");
                    settings.Remove("last_desc");
                    foreach (string d in Directory.GetDirectories(App.DataRoot()))
                    {
                        string c = Path.Combine(d, "缓存");
                        if (Directory.Exists(c)) { try { Directory.Delete(c, true); } catch { } }
                    }
                    try
                    {
                        string db = Path.Combine(App.CoreDir(), "cache.db");
                        if (File.Exists(db)) File.Delete(db);
                    }
                    catch { }
                    Directory.CreateDirectory(App.DataRoot());
                    File.WriteAllText(marker, App.ShellVersion);
                }
            }
            catch { }

            string savedFlag = App.GetStr(settings, "last_flag", "");
            bool launchedGuess = false;
            string flag;
            if (savedFlag.Length > 0)
            {
                flag = savedFlag;
                App.LastProxyDesc = App.GetStr(settings, "last_desc", App.LastProxyDesc);
                App.ProxyBlocked = flag.Contains("127.0.0.1:9");
                App.CoreInUse = flag.Contains("127.0.0.1:" + App.CorePort);
                launchedGuess = true;
            }
            else if (App.GetBool(settings, "core_enabled", true)
                && File.Exists(Path.Combine(App.CoreDir(), "config.yaml")))
            {
                flag = "--proxy-server=http://127.0.0.1:" + App.CorePort;
                App.LastProxyDesc = "内置订阅代理 127.0.0.1:" + App.CorePort;
                App.ProxyBlocked = false;
                App.CoreInUse = true;
                launchedGuess = true;
            }
            else
            {
                flag = ProxyDetect.DetectProxyFlag(settings);
            }
            App.Set(settings, "last_flag", flag);
            App.Set(settings, "last_desc", App.LastProxyDesc);
            App.SaveSettings(settings);
            if (launchedGuess && App.GetBool(settings, "core_enabled", true))
            {
                Thread t = new Thread(delegate () { ProxyCore.StartCore(30); });
                t.IsBackground = true;
                t.Start();
            }

            string gpuFlags = "--ignore-gpu-blocklist --enable-gpu-rasterization --enable-zero-copy "
                + "--use-angle=d3d11 --num-raster-threads=4 --lang=zh-CN "
                + "--force-webrtc-ip-handling-policy=disable_non_proxied_udp --dns-prefetch-disable";
            string browserArgs = (flag + " " + gpuFlags).Trim();

            var form = new MainForm(settings, flag, browserArgs, launchedGuess);
            form.FormClosed += delegate { ProxyCore.StopCore(); if (locker != null) locker.Stop(); };
            Application.Run(form);
        }
    }
}
