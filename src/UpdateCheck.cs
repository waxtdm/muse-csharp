using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace Muse
{
    // 自动更新：从 GitHub 拉 version.json 比对版本，有新版就拉源码包、本地重新编译、替换后重启。
    // 走源码更新是因为 mihomo.exe / geoip.metadb 是二进制，不能进 GitHub 的文本推送；
    // 本机已有这两个文件（res/ 或 Muse数据/代理核心/），编译时直接复用，不用重新下载。
    static class UpdateCheck
    {
        public static bool IsNewer(string remote, string local)
        {
            try
            {
                string[] a = remote.Split('.');
                string[] b = local.Split('.');
                int n = Math.Max(a.Length, b.Length);
                for (int i = 0; i < n; i++)
                {
                    int x = 0, y = 0;
                    if (i < a.Length) int.TryParse(a[i], out x);
                    if (i < b.Length) int.TryParse(b[i], out y);
                    if (x != y) return x > y;
                }
            }
            catch { }
            return false;
        }

        public static JObject FetchVersion()
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "Muse-Updater");
                string json = wc.DownloadString(App.UpdateUrl);
                return JObject.Parse(json);
            }
        }


        // 不依赖 ZipArchive/ZipFile（它们在单独的程序集里，老机器不一定有），
        // 直接按 zip 格式读中央目录、用 System.dll 自带的 DeflateStream 解压（方法 0 存、方法 8 缩）
        public static void ExtractZip(string zipPath, string destDir)
        {
            byte[] buf = File.ReadAllBytes(zipPath);
            int eocd = -1;
            for (int i = buf.Length - 22; i >= 0 && i >= buf.Length - 22 - 65536; i--)
            {
                if (buf[i] == 0x50 && buf[i + 1] == 0x4b && buf[i + 2] == 0x05 && buf[i + 3] == 0x06) { eocd = i; break; }
            }
            if (eocd < 0) throw new Exception("不是有效的 zip 文件");
            int count = BitConverter.ToUInt16(buf, eocd + 10);
            int cdOff = (int)BitConverter.ToUInt32(buf, eocd + 16);
            int p = cdOff;
            for (int n = 0; n < count; n++)
            {
                if (p + 46 > buf.Length || BitConverter.ToUInt32(buf, p) != 0x02014b50) throw new Exception("zip 中央目录损坏");
                int method = BitConverter.ToUInt16(buf, p + 10);
                int csize = (int)BitConverter.ToUInt32(buf, p + 20);
                int nameLen = BitConverter.ToUInt16(buf, p + 28);
                int extraLen = BitConverter.ToUInt16(buf, p + 30);
                int commentLen = BitConverter.ToUInt16(buf, p + 32);
                int lho = (int)BitConverter.ToUInt32(buf, p + 42);
                string name = Encoding.UTF8.GetString(buf, p + 46, nameLen);
                p += 46 + nameLen + extraLen + commentLen;
                string target = Path.Combine(destDir, name.Replace('/', Path.DirectorySeparatorChar));
                string full = Path.GetFullPath(target);
                if (!full.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                if (lho + 30 > buf.Length || BitConverter.ToUInt32(buf, lho) != 0x04034b50) throw new Exception("zip 本地文件头损坏");
                int lhName = BitConverter.ToUInt16(buf, lho + 26);
                int lhExtra = BitConverter.ToUInt16(buf, lho + 28);
                int dataOff = lho + 30 + lhName + lhExtra;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (FileStream f = new FileStream(target, FileMode.Create, FileAccess.Write))
                {
                    if (method == 0) f.Write(buf, dataOff, csize);
                    else if (method == 8)
                    {
                        using (MemoryStream ms = new MemoryStream(buf, dataOff, csize))
                        using (System.IO.Compression.DeflateStream ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
                        {
                            ds.CopyTo(f);
                        }
                    }
                    else throw new Exception("zip 里有不支持的压缩方法：" + method);
                }
            }
        }

        // 程序包根目录（build.bat、src、res 所在的那一级）；exe 在 bin\ 里，根目录是 bin 的上一级
        public static string AppRoot()
        {
            string root = Application.StartupPath;
            try
            {
                string parent = Directory.GetParent(root).FullName;
                if (File.Exists(Path.Combine(parent, "build.bat"))) return parent;
            }
            catch { }
            return root;
        }

        public static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // 更新器脚本（放在新编译出的目录里，启动时传：update.bat "包根目录" "新编译目录"）
        // 等旧进程退出 -> 把新树拷过去 -> 启动新 exe -> 退出；临时目录由新启动的程序自己清
        public static string UpdateBatText()
        {
            return "@echo off\r\n"
                + "setlocal EnableDelayedExpansion\r\n"
                + "set APP=%~1\r\n"
                + "set SRC=%~2\r\n"
                + "set N=0\r\n"
                + ":wait\r\n"
                + "tasklist /FI \"IMAGENAME eq Muse.exe\" 2>nul | find /I \"Muse.exe\" >nul\r\n"
                + "if not errorlevel 1 (\r\n"
                + "  set /a N+=1\r\n"
                + "  if !N! GEQ 30 goto copy\r\n"
                + "  timeout /t 1 /nobreak >nul\r\n"
                + "  goto wait\r\n"
                + ")\r\n"
                + ":copy\r\n"
                + "xcopy /y /e \"%SRC%\\*\" \"%APP%\\\"\r\n"
                + "if exist \"%APP%\\bin\\Muse.exe\" (start \"\" \"%APP%\\bin\\Muse.exe\") else (start \"\" \"%APP%\\Muse.exe\")\r\n"
                + "exit\r\n";
        }
    }
}
