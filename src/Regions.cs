using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Muse
{
    // 地区识别 + 节点延迟（对应 py 的 REGION_KEYS / FLAG_REGIONS / region_of / node_delay / main_selector_group）
    static class Regions
    {
        public static readonly string[][] RegionKeys = new string[][]
        {
            new string[] { "日本", "日本", "东京", "大阪", "名古屋", "JP", "JAPAN", "TOKYO", "OSAKA" },
            new string[] { "香港", "香港", "HK", "HONGKONG", "HONG KONG" },
            new string[] { "台湾", "台湾", "台北", "新北", "TW", "TAIWAN", "TAIPEI" },
            new string[] { "新加坡", "新加坡", "狮城", "SG", "SINGAPORE" },
            new string[] { "美国", "美国", "洛杉矶", "纽约", "芝加哥", "西雅图", "硅谷", "达拉斯", "US", "USA", "AMERICA" },
            new string[] { "韩国", "韩国", "首尔", "KR", "KOREA", "SEOUL" },
            new string[] { "英国", "英国", "伦敦", "UK", "GB", "LONDON", "BRITAIN" },
            new string[] { "德国", "德国", "法兰克福", "DE", "GERMANY", "FRANKFURT" },
            new string[] { "法国", "法国", "巴黎", "FR", "FRANCE", "PARIS" },
            new string[] { "加拿大", "加拿大", "CA", "CANADA" },
            new string[] { "澳大利亚", "澳大利亚", "澳洲", "悉尼", "墨尔本", "AU", "AUSTRALIA", "SYDNEY" },
            new string[] { "印度", "印度", "IN", "INDIA" },
            new string[] { "俄罗斯", "俄罗斯", "莫斯科", "RU", "RUSSIA" },
            new string[] { "土耳其", "土耳其", "TR", "TURKEY" },
            new string[] { "荷兰", "荷兰", "阿姆斯特丹", "NL", "NETHERLANDS" },
            new string[] { "意大利", "意大利", "IT", "ITALY" },
            new string[] { "西班牙", "西班牙", "ES", "SPAIN" },
            new string[] { "泰国", "泰国", "曼谷", "TH", "THAILAND" },
            new string[] { "越南", "越南", "VN", "VIETNAM" },
            new string[] { "马来西亚", "马来西亚", "MY", "MALAYSIA" },
            new string[] { "菲律宾", "菲律宾", "PH", "PHILIPPINES" },
            new string[] { "印度尼西亚", "印尼", "印度尼西亚", "ID", "INDONESIA" },
            new string[] { "阿根廷", "阿根廷", "AR", "ARGENTINA" },
            new string[] { "巴西", "巴西", "BR", "BRAZIL" },
        };

        public static readonly string[][] FlagRegions = new string[][]
        {
            new string[] { "🇭🇰", "香港" }, new string[] { "🇹🇼", "台湾" }, new string[] { "🇯🇵", "日本" },
            new string[] { "🇸🇬", "新加坡" }, new string[] { "🇺🇸", "美国" }, new string[] { "🇰🇷", "韩国" },
            new string[] { "🇬🇧", "英国" }, new string[] { "🇩🇪", "德国" }, new string[] { "🇫🇷", "法国" },
            new string[] { "🇨🇦", "加拿大" }, new string[] { "🇦🇺", "澳大利亚" }, new string[] { "🇮🇳", "印度" },
            new string[] { "🇷🇺", "俄罗斯" }, new string[] { "🇹🇷", "土耳其" }, new string[] { "🇳🇱", "荷兰" },
            new string[] { "🇮🇹", "意大利" }, new string[] { "🇪🇸", "西班牙" }, new string[] { "🇹🇭", "泰国" },
            new string[] { "🇻🇳", "越南" }, new string[] { "🇲🇾", "马来西亚" }, new string[] { "🇵🇭", "菲律宾" },
            new string[] { "🇮🇩", "印度尼西亚" }, new string[] { "🇦🇷", "阿根廷" }, new string[] { "🇧🇷", "巴西" },
            new string[] { "🇨🇳", "中国" },
        };

        static readonly string[] JunkWords = new string[]
        {
            "流量", "到期", "重置", "官网", "公告", "网址", "套餐", "订阅", "距离下次"
        };

        static bool IsAscii(string s)
        {
            foreach (char c in s) if (c > 127) return false;
            return true;
        }

        public static string RegionOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (string[] fr in FlagRegions)
            {
                if (name.Contains(fr[0])) return fr[1];
            }
            string upper = name.ToUpperInvariant();
            foreach (string[] row in RegionKeys)
            {
                string region = row[0];
                for (int i = 1; i < row.Length; i++)
                {
                    string k = row[i];
                    if (IsAscii(k))
                    {
                        if (k.Length <= 2)
                        {
                            string[] parts = Regex.Split(upper, "[^A-Z]+");
                            if (parts.Contains(k)) return region;
                        }
                        else if (upper.Contains(k)) return region;
                    }
                    else if (name.Contains(k)) return region;
                }
            }
            return null;
        }

        public static bool IsJunkNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string w in JunkWords)
            {
                if (name.Contains(w)) return true;
            }
            return false;
        }

        // 测单个节点延迟（调核心 /proxies/{name}/delay），失败返回 null
        public static int? NodeDelay(string name, int timeoutMs = 5000)
        {
            try
            {
                string q = "url=" + Uri.EscapeDataString("http://www.gstatic.com/generate_204")
                    + "&timeout=" + timeoutMs;
                JToken data = ProxyCore.CoreApi("/proxies/" + Uri.EscapeDataString(name) + "/delay?" + q);
                JToken d = data["delay"];
                if (d != null && d.Type == JTokenType.Integer)
                {
                    int v = (int)d;
                    if (v > 0) return v;
                }
            }
            catch { }
            return null;
        }

        // 找主策略组：非 GLOBAL 的 Selector，优先被引用次数多、节点多的
        // 返回 (组名, 组信息 JObject)，找不到返回 (null, null)
        public static Tuple<string, JObject> MainSelectorGroup()
        {
            JToken data = ProxyCore.CoreApi("/proxies");
            JObject proxies = data["proxies"] as JObject;
            if (proxies == null) return Tuple.Create<string, JObject>(null, null);
            var groups = new List<Tuple<string, JObject>>();
            foreach (JProperty p in proxies.Properties())
            {
                JObject info = p.Value as JObject;
                if (info == null) continue;
                JArray all = info["all"] as JArray;
                if ((string)info["type"] == "Selector" && all != null && all.Count > 0
                    && p.Name != "GLOBAL")
                    groups.Add(Tuple.Create(p.Name, info));
            }
            if (groups.Count == 0)
            {
                foreach (JProperty p in proxies.Properties())
                {
                    JObject info = p.Value as JObject;
                    if (info == null) continue;
                    JArray all = info["all"] as JArray;
                    if ((string)info["type"] == "Selector" && all != null && all.Count > 0)
                        groups.Add(Tuple.Create(p.Name, info));
                }
            }
            if (groups.Count == 0) return Tuple.Create<string, JObject>(null, null);
            var refs = new Dictionary<string, int>();
            foreach (JProperty p in proxies.Properties())
            {
                JObject info = p.Value as JObject;
                if (info == null) continue;
                string now = (string)info["now"];
                if (!string.IsNullOrEmpty(now))
                {
                    int c;
                    refs.TryGetValue(now, out c);
                    refs[now] = c + 1;
                }
            }
            groups.Sort(delegate (Tuple<string, JObject> a, Tuple<string, JObject> b)
            {
                int ra, rb;
                refs.TryGetValue(a.Item1, out ra);
                refs.TryGetValue(b.Item1, out rb);
                int cmp = rb.CompareTo(ra);
                if (cmp != 0) return cmp;
                int la = ((JArray)a.Item2["all"]).Count;
                int lb = ((JArray)b.Item2["all"]).Count;
                return lb.CompareTo(la);
            });
            return groups[0];
        }
    }
}
