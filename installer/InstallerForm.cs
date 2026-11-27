using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MuseSetup
{
    // Muse 一键安装程序：解压内嵌的完整程序包 -> 自动跑 build.bat 编译 -> 桌面建快捷方式 -> 启动
    class InstallerForm : Form
    {
        TextBox _pathBox;
        TextBox _logBox;
        Button _installBtn;
        Button _closeBtn;
        Label _statusLabel;

        public InstallerForm()
        {
            Text = "Muse 安装程序";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 380);

            Label l1 = new Label();
            l1.Text = "安装到：";
            l1.Location = new Point(14, 16);
            l1.Size = new Size(60, 20);
            Controls.Add(l1);

            _pathBox = new TextBox();
            _pathBox.Location = new Point(76, 14);
            _pathBox.Size = new Size(380, 22);
            _pathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Muse");
            Controls.Add(_pathBox);

            Button browse = new Button();
            browse.Text = "浏览...";
            browse.Location = new Point(466, 12);
            browse.Size = new Size(80, 26);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择 Muse 的安装文件夹";
                    d.SelectedPath = _pathBox.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) _pathBox.Text = d.SelectedPath;
                }
            };
            Controls.Add(browse);

            _installBtn = new Button();
            _installBtn.Text = "开始安装";
            _installBtn.Location = new Point(14, 48);
            _installBtn.Size = new Size(110, 30);
            _installBtn.Click += delegate { StartInstall(); };
            Controls.Add(_installBtn);

            _closeBtn = new Button();
            _closeBtn.Text = "关闭";
            _closeBtn.Location = new Point(134, 48);
            _closeBtn.Size = new Size(80, 30);
            _closeBtn.Click += delegate { Close(); };
            Controls.Add(_closeBtn);

            _statusLabel = new Label();
            _statusLabel.Location = new Point(224, 54);
            _statusLabel.Size = new Size(322, 20);
            _statusLabel.Text = "点“开始安装”后自动解压、编译、建桌面快捷方式。";
            Controls.Add(_statusLabel);

            _logBox = new TextBox();
            _logBox.Location = new Point(14, 88);
            _logBox.Size = new Size(532, 278);
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            Controls.Add(_logBox);
        }

        void Log(string s)
        {
            try
            {
                BeginInvoke(new Action(delegate
                {
                    _logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + s + "\r\n");
                }));
            }
            catch { }
        }

        void SetStatus(string s)
        {
            try { BeginInvoke(new Action(delegate { _statusLabel.Text = s; })); } catch { }
        }

        void StartInstall()
        {
            string target = _pathBox.Text.Trim();
            if (target.Length == 0) { MessageBox.Show(this, "请先填安装文件夹。", "安装程序", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _installBtn.Enabled = false;
            _logBox.Clear();
            Task.Run(delegate ()
            {
                string tmp = Path.Combine(Path.GetTempPath(), "MuseInstall-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    SetStatus("正在解压安装包...");
                    Log("正在解压安装包...");
                    ExtractPayload(tmp);
                    SetStatus("正在复制文件...");
                    Log("正在复制文件到 " + target + "...");
                    CopyTree(tmp, target);
                    SetStatus("正在编译（约一两分钟）...");
                    Log("正在编译，约一两分钟，请稍候...");
                    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"build.bat > build_stdout.log 2>&1 < nul\"");
                    psi.WorkingDirectory = target;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process p = Process.Start(psi);
                    p.WaitForExit();
                    string exe = Path.Combine(target, "bin", "Muse.exe");
                    if (!File.Exists(exe) || new FileInfo(exe).Length < 10000000)
                    {
                        string log = "";
                        try { log = File.ReadAllText(Path.Combine(target, "build.log")); } catch { }
                        if (log.Length > 600) log = log.Substring(0, 600) + "...";
                        throw new Exception("编译失败，没有编出 Muse.exe。\r\n" + log);
                    }
                    Log("编译完成。");
                    SetStatus("正在创建桌面快捷方式...");
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    string lnk = Path.Combine(desktop, "Muse.lnk");
                    try
                    {
                        CreateDesktopShortcut(lnk, exe, Path.GetDirectoryName(exe));
                        Log("桌面快捷方式已创建：" + lnk);
                    }
                    catch (Exception ex)
                    {
                        Log("桌面快捷方式创建失败：" + ex.Message + "（程序本身已装好，可手动右键 Muse.exe -> 发送到 -> 桌面快捷方式）");
                    }
                    SetStatus("安装完成，正在启动 Muse...");
                    Log("安装完成，正在启动 Muse...");
                    try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe) }); } catch { }
                    try { Directory.Delete(tmp, true); } catch { }
                    BeginInvoke(new Action(delegate { _installBtn.Enabled = true; _installBtn.Text = "重新安装"; }));
                }
                catch (Exception ex)
                {
                    SetStatus("安装失败");
                    Log("安装失败：" + ex.Message);
                    BeginInvoke(new Action(delegate { _installBtn.Enabled = true; }));
                }
            });
        }

        // 从本程序内嵌的 payload.zip 解到目标目录（不依赖 ZipArchive，手写中央目录 + DeflateStream）
        public static void ExtractPayload(string destDir)
        {
            Stream rs = typeof(InstallerForm).Assembly.GetManifestResourceStream("MuseSetup.payload.zip");
            if (rs == null) throw new Exception("安装包里找不到内嵌的程序包。");
            byte[] buf;
            using (MemoryStream ms = new MemoryStream())
            {
                rs.CopyTo(ms);
                buf = ms.ToArray();
            }
            ExtractZipBytes(buf, destDir);
        }

        public static void ExtractZipBytes(byte[] buf, string destDir)
        {
            int eocd = -1;
            for (int i = buf.Length - 22; i >= 0 && i >= buf.Length - 22 - 65536; i--)
            {
                if (buf[i] == 0x50 && buf[i + 1] == 0x4b && buf[i + 2] == 0x05 && buf[i + 3] == 0x06) { eocd = i; break; }
            }
            if (eocd < 0) throw new Exception("安装包损坏：不是有效的 zip 文件");
            int count = BitConverter.ToUInt16(buf, eocd + 10);
            int cdOff = (int)BitConverter.ToUInt32(buf, eocd + 16);
            int p = cdOff;
            for (int n = 0; n < count; n++)
            {
                if (p + 46 > buf.Length || BitConverter.ToUInt32(buf, p) != 0x02014b50) throw new Exception("安装包损坏：zip 中央目录损坏");
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
                if (lho + 30 > buf.Length || BitConverter.ToUInt32(buf, lho) != 0x04034b50) throw new Exception("安装包损坏：zip 本地文件头损坏");
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
                    else throw new Exception("安装包损坏：zip 里有不支持的压缩方法：" + method);
                }
            }
        }

        public static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src))
                CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // 桌面快捷方式（WScript.Shell 晚期绑定，不用额外引用）
        public static void CreateDesktopShortcut(string lnkPath, string exePath, string workDir)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new Exception("系统里没有 WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            try
            {
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exePath });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workDir });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Muse" });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { exePath + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(link); } catch { }
                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
        }
    }
}
