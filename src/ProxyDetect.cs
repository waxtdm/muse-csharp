using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Muse
{
    // 代理决策链（对应 py 的 _detect_impl / detect_proxy_flag）：
    // 内置核心优先 -> 系统代理/PAC/本机端口 -> 直连 -> 黑洞 127.0.0.1:9
    static class ProxyDetect
    {
        public static string DetectImpl(JObject settings)
        {
            App.ProxyBlocked = false;
            string coreNote = "";
            if (App.GetBool(settings, "core_enabled", true))
            {
                ProxyCore.MaybeRefreshSubscription(settings);
                if (!ProxyCore.StartCore(6))
                {
                    var urls = new List<string>();
                    foreach (JObject e in App.GetSubUrls(settings))
                    {
                        string u = (string)e["url"];
                        if (!string.IsNullOrEmpty(u)) urls.Add(u);
                    }
                    if (urls.Count > 0 && !File.Exists(Path.Combine(App.CoreDir(), "config.yaml")))
                    {
                        ProxyCore.DownloadSubscriptions(urls);
                        ProxyCore.StartCore(6);
                    }
                    else if (File.Exists(Path.Combine(App.CoreDir(), "config.yaml")))
                    {
                        ProxyCore.StartCore(6);
                    }
                }
                if (App.CoreInUse || ProxyCore.CoreAlive())
                {
                    App.CoreInUse = true;
                    App.LastProxyDesc = "内置订阅代理 127.0.0.1:" + App.CorePort;
                    return "--proxy-server=http://127.0.0.1:" + App.CorePort;
                }
                coreNote = "内置订阅代理没起来（" + App.CoreState + "）";
            }
            if (App.GetBool(settings, "use_local_proxy", false))
            {
                string prefix = coreNote.Length > 0 ? coreNote + "，回退：" : "";
                string pac = ProxyCore.ReadPacUrl();
                if (pac.Length > 0)
                {
                    App.LastProxyDesc = prefix + "本地代理：PAC " + pac;
                    return "--proxy-pac-url=" + pac;
                }
                string cand = ProxyCore.ReadSystemProxy();
                string candDesc = cand.Length > 0 ? "系统代理 " + cand : "";
                if (cand.Length == 0)
                {
                    cand = ProxyCore.ProbeLocalPort();
                    candDesc = cand.Length > 0 ? "本机端口 " + cand : "";
                }
                if (cand.Length > 0 && ProxyCore.ProbeWorks(cand))
                {
                    App.LastProxyDesc = prefix + "本地代理：" + candDesc + "（已测通）";
                    return "--proxy-server=" + cand;
                }
                if (ProxyCore.ProbeWorks(""))
                {
                    App.LastProxyDesc = prefix + (cand.Length > 0 ? "本地代理不通，" : "没找到本地代理，") + "已走直连（已测通）";
                    return "--no-proxy-server";
                }
                App.LastProxyDesc = prefix + "本地代理和直连都不通（已拦截，网络恢复后会自动重试切换）";
                App.ProxyBlocked = true;
                return "--proxy-server=http://127.0.0.1:9";
            }
            App.LastProxyDesc = (coreNote.Length > 0 ? coreNote + "；" : "内置订阅代理未启用；")
                + "本地代理未勾选（按设置不走直连，已拦截：去代理设置勾选本地代理，或添加订阅启用内置代理）";
            App.ProxyBlocked = true;
            return "--proxy-server=http://127.0.0.1:9";
        }

        public static string DetectProxyFlag(JObject settings)
        {
            string flag = DetectImpl(settings);
            SaveLastRoute(flag);
            return flag;
        }

        public static void SaveLastRoute(string flag)
        {
            try
            {
                JObject data = App.LoadSettings();
                App.Set(data, "last_flag", flag);
                App.Set(data, "last_desc", App.LastProxyDesc);
                App.SaveSettings(data);
            }
            catch { }
        }
    }
}
