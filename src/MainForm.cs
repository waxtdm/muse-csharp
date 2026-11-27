using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace Muse
{
    // 主窗口：WinForms + WebView2（对应 py 的 QMainWindow + QWebEngineView）
    // 每个账号一个独立的 WebView2 环境（UserDataFolder 隔离），首页 https://muse.ai
    public class MainForm : Form
    {
        readonly JObject _settings;
        readonly string _browserArgs;
        readonly bool _launchedGuess;
        readonly List<string> _accounts = new List<string>();
        readonly Dictionary<string, WebView2> _views = new Dictionary<string, WebView2>();
        readonly Dictionary<string, CoreWebView2Environment> _envs = new Dictionary<string, CoreWebView2Environment>();
        readonly Dictionary<string, Button> _accButtons = new Dictionary<string, Button>();
        readonly HashSet<string> _recentDownloads = new HashSet<string>();
        readonly Dictionary<WebView2, LoadState> _loadStates = new Dictionary<WebView2, LoadState>();

        FlowLayoutPanel _bar;
        Panel _topPanel;
        Label _nodeLabel;
        Panel _host;
        StatusStrip _status;
        ToolStripStatusLabel _statusLabel, _lineLabel, _speedLabel, _clockLabel, _dlLabel;
        ToolStripProgressBar _dlBar;
        System.Windows.Forms.Timer _clockTimer, _nodeTimer, _lineTimer, _guardTimer, _spaceTimer, _cacheTimer;
        bool _loadPromptDone = false;
        NotifyIcon _tray;
        bool _trayHintShown = false;
        string _currentAccount;

        class LoadState
        {
            public DateTime Since;
            public int Retries;
            public bool WasIn;
            public bool DropNoted;
            public string Account = "";
        }

        // 失效切换状态（对应 py 的 fail_state）
        class FailState
        {
            public int N;
            public bool Busy;
            public bool Alerted;
            public DateTime Since;
            public bool NoRegNoted;
        }
        readonly FailState _fail = new FailState();
        // 线路巡检状态（对应 py 的 line_state）
        class LineState
        {
            public string Name;
            public DateTime DelayT;
            public bool Busy;
            public string Group;
            public int Mis;
        }
        readonly LineState _lineState = new LineState();
        // 守卫状态（对应 py 的 guard_state）
        class GuardState
        {
            public bool Busy;
            public int Fails;
            public bool RefreshNoted;
            public bool AutoSwitched;
            public bool ReviveBusy;
        }
        readonly GuardState _guard = new GuardState();

        public MainForm(JObject settings, string flag, string browserArgs, bool launchedGuess)
        {
            _settings = settings;
            _browserArgs = browserArgs;
            _launchedGuess = launchedGuess;
            Text = "Muse";
            // 左上角图标：登录页那个胖乎乎的小人（base64 内嵌在 AppIcon.cs，免文件，自动更新也能带上）
            try
            {
                byte[] iconData = Convert.FromBase64String(AppIcon.IcoBase64);
                using (MemoryStream ms = new MemoryStream(iconData))
                {
                    Icon = new Icon(ms);
                }
            }
            catch { }
            Size = new Size(1280, 900);
            StartPosition = FormStartPosition.CenterScreen;

            _bar = new FlowLayoutPanel();
            _bar.AutoSize = true;
            _bar.Padding = new Padding(6);
            _topPanel = new Panel();
            _topPanel.Dock = DockStyle.Top;
            _topPanel.Height = 36;
            _topPanel.BackColor = Color.FromArgb(199, 237, 204); // 豆沙绿护眼色
            _bar.Dock = DockStyle.Fill;
            _bar.AutoSize = false;
            _bar.BackColor = Color.FromArgb(199, 237, 204);
            _topPanel.Controls.Add(_bar);
            _nodeLabel = new Label();
            _nodeLabel.Dock = DockStyle.Right;
            _nodeLabel.Width = 480;
            _nodeLabel.AutoSize = false;
            _nodeLabel.TextAlign = ContentAlignment.MiddleRight;
            _nodeLabel.Font = new Font("微软雅黑", 9f);
            _nodeLabel.Text = "—";
            _topPanel.Controls.Add(_nodeLabel);
            Controls.Add(_topPanel);

            _host = new Panel();
            _host.Dock = DockStyle.Fill;
            Controls.Add(_host);

            _status = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("");
            _statusLabel.Spring = true;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _lineLabel = new ToolStripStatusLabel("—");
            _speedLabel = new ToolStripStatusLabel("—");
            _clockLabel = new ToolStripStatusLabel("");
            _clockLabel.Font = new Font(_clockLabel.Font, FontStyle.Bold);
            _dlLabel = new ToolStripStatusLabel("");
            _dlLabel.Visible = false;
            _dlBar = new ToolStripProgressBar();
            _dlBar.Width = 180;
            _dlBar.Visible = false;
            _status.Items.Add(_statusLabel);
            _status.Items.Add(_lineLabel);
            _status.Items.Add(_speedLabel);
            _status.Items.Add(_clockLabel);
            _status.Items.Add(_dlLabel);
            _status.Items.Add(_dlBar);
            Controls.Add(_status);

            LoadAccounts();
            CleanupStartupCache();
            BuildBar();
            RestoreGeometry();
            FormClosing += delegate { SaveGeometry(); };

            // 最小化后藏到右下角托盘：点最小化按钮即隐藏窗口，双击托盘图标恢复
            _tray = new NotifyIcon();
            _tray.Icon = Icon ?? SystemIcons.Application;
            _tray.Text = "Muse";
            _tray.Visible = true;
            var trayMenu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("显示窗口");
            showItem.Click += delegate { ShowFromTray(); };
            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { Close(); };
            trayMenu.Items.Add(showItem);
            trayMenu.Items.Add(exitItem);
            _tray.ContextMenuStrip = trayMenu;
            _tray.DoubleClick += delegate { ShowFromTray(); };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Hide();
                    if (!_trayHintShown)
                    {
                        _trayHintShown = true;
                        _tray.ShowBalloonTip(3000, "Muse", "已隐藏到右下角，双击托盘图标恢复。", ToolTipIcon.Info);
                    }
                }
            };
            FormClosing += delegate { try { _tray.Visible = false; _tray.Dispose(); } catch { } };
            Shown += delegate { CheckUpdate(false); };

            _clockTimer = new System.Windows.Forms.Timer();
            _clockTimer.Interval = 1000;
            _clockTimer.Tick += delegate { _clockLabel.Text = DateTime.Now.ToString("HH:mm:ss"); UpdateNodeLabel(); };
            _clockTimer.Start();
            _clockLabel.Text = DateTime.Now.ToString("HH:mm:ss");

            _nodeTimer = new System.Windows.Forms.Timer();
            _nodeTimer.Interval = 60000;
            _nodeTimer.Tick += delegate { CheckNodes(); };
            _nodeTimer.Start();
            var t0 = new System.Windows.Forms.Timer();
            t0.Interval = 20000;
            t0.Tick += delegate { t0.Stop(); CheckNodes(); };
            t0.Start();

            _lineTimer = new System.Windows.Forms.Timer();
            _lineTimer.Interval = 5000;
            _lineTimer.Tick += delegate { PollLine(); };
            _lineTimer.Start();
            var t1 = new System.Windows.Forms.Timer();
            t1.Interval = 3000;
            t1.Tick += delegate { t1.Stop(); PollLine(); };
            t1.Start();

            _guardTimer = new System.Windows.Forms.Timer();
            _guardTimer.Interval = 20000;
            _guardTimer.Tick += delegate { GuardTick(); };
            _guardTimer.Start();

            _spaceTimer = new System.Windows.Forms.Timer();
            _spaceTimer.Interval = 3600000;
            _spaceTimer.Tick += delegate { SpaceCheck(); };
            _spaceTimer.Start();
            SpaceCheck();

            _cacheTimer = new System.Windows.Forms.Timer();
            _cacheTimer.Interval = 6 * 3600000;
            _cacheTimer.Tick += delegate { CacheCheck(); };
            _cacheTimer.Start();

            CheckAccountClash();
            Shown += delegate
            {
                SwitchTo(_accounts[0]);
                PrewarmNext(1);
                if (_launchedGuess)
                {
                    Task.Run(delegate ()
                    {
                        bool wasBlocked = App.ProxyBlocked;
                        Thread.Sleep(1500);
                        string nf = ProxyDetect.DetectProxyFlag(_settings);
                        App.Set(_settings, "last_flag", nf);
                        App.Set(_settings, "last_desc", App.LastProxyDesc);
                        App.SaveSettings(_settings);
                        if (wasBlocked && nf != flag && !nf.Contains("127.0.0.1:9"))
                        {
                            BeginInvoke(new Action(delegate { Revive(); }));
                        }
                    });
                }
                Task.Run(delegate () { TrafficWorker(); });
            };
        }

        void ShowStatus(string text, int ms)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(delegate { ShowStatus(text, ms); })); return; }
            _statusLabel.Text = text;
        }

        void SetLine(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(delegate { SetLine(text); })); return; }
            _lineLabel.Text = text;
        }

        // 右上角：当前节点 + 速率 + 时间（和底栏同一份数据）
        void UpdateNodeLabel()
        {
            if (IsDisposed || _nodeLabel == null) return;
            if (InvokeRequired) { BeginInvoke(new Action(delegate { UpdateNodeLabel(); })); return; }
            string node = _lineLabel.Text;
            string speed = _speedLabel.Text;
            string clock = _clockLabel.Text;
            _nodeLabel.Text = node + "  " + speed + "  " + clock;
        }

        // ---------- 账号 ----------
        void LoadAccounts()
        {
            bool healthy = true;
            try
            {
                string p = App.AccountsPath();
                if (File.Exists(p))
                {
                    JArray arr = JArray.Parse(File.ReadAllText(p, Encoding.UTF8));
                    foreach (JToken t in arr)
                    {
                        if (t.Type != JTokenType.String) continue;
                        string n = ((string)t).Trim();
                        if (n.Length == 0 || n == "." || n == ".." || n == "代理核心") continue;
                        if (n.IndexOfAny(new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) continue;
                        _accounts.Add(n);
                    }
                    if (_accounts.Count == 0) healthy = false;
                }
            }
            catch { healthy = false; }
            if (_accounts.Count == 0) { _accounts.Add("账号1"); healthy = false; }
            SaveAccounts();
            if (healthy)
            {
                try
                {
                    foreach (string d in Directory.GetDirectories(App.DataRoot()))
                    {
                        string name = Path.GetFileName(d);
                        if (!_accounts.Contains(name) && name != "代理核心")
                        {
                            try { Directory.Delete(d, true); } catch { }
                        }
                    }
                }
                catch { }
            }
        }

        void SaveAccounts()
        {
            var arr = new JArray();
            foreach (string n in _accounts) arr.Add(n);
            string tmp = App.AccountsPath() + ".tmp";
            File.WriteAllText(tmp, arr.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
            if (File.Exists(App.AccountsPath())) File.Delete(App.AccountsPath());
            File.Move(tmp, App.AccountsPath());
        }

        void CleanupStartupCache()
        {
            foreach (string n in _accounts)
            {
                string cdir = Path.Combine(App.DataRoot(), n, "缓存");
                try
                {
                    if (Directory.Exists(cdir) && App.DirSize(cdir) > App.CacheLimit)
                        Directory.Delete(cdir, true);
                }
                catch { }
            }
        }

        void CheckAccountClash()
        {
            var seen = new Dictionary<string, string>();
            foreach (string n in _accounts)
            {
                string key = Path.Combine(App.DataRoot(), n, "存储").ToLowerInvariant();
                string prev;
                if (seen.TryGetValue(key, out prev))
                {
                    MessageBox.Show("「" + prev + "」和「" + n + "」的数据文件夹是同一个，登录会串号！请删掉其中一个账号、换个名字重加。",
                        "账号数据撞车", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                seen[key] = n;
            }
        }

        void BuildBar()
        {
            _bar.Controls.Clear();
            _accButtons.Clear();
            AddBarButton("代理设置", delegate { new ProxyDialog(this, _settings).ShowDialog(this); });
            AddBarButton("检查更新", delegate { CheckUpdate(true); });
            AddBarButton("刷新", delegate { WebView2 v = CurrentView(); if (v != null) v.Reload(); });
            AddBarButton("主页", delegate { WebView2 v = CurrentView(); if (v != null) v.Source = new Uri("https://muse.ai"); });
            foreach (string name in _accounts)
            {
                string n = name;
                Button b = new Button();
                b.Text = n;
                b.AutoSize = true;
                b.Click += delegate { SwitchTo(n); };
                _bar.Controls.Add(b);
                _accButtons[n] = b;
            }
            AddBarButton("＋ 添加账号", delegate { AddAccount(); });
            AddBarButton("删除当前账号", delegate { DeleteCurrent(); });
            UpdateLineLabel();
        }

        Button AddBarButton(string text, Action onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Click += delegate { onClick(); };
            _bar.Controls.Add(b);
            return b;
        }

        void UpdateLineLabel()
        {
            string desc = App.LastProxyDesc;
            string shortText;
            if (App.CoreInUse) shortText = "内置代理";
            else
            {
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(desc, @"\d+\.\d+\.\d+\.\d+:\d+");
                if (m.Success) shortText = m.Value;
                else if (desc.Contains("PAC")) shortText = "PAC";
                else if (desc.Contains("拦截") || desc.Contains("不通")) shortText = "无代理";
                else shortText = "—";
            }
            _lineLabel.Text = shortText;
        }

        void AddAccount()
        {
            string name = Prompt("添加账号", "给新账号起个名字：");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) return;
            if (name == "." || name == ".." || name == "代理核心"
                || name.EndsWith(" ") || name.EndsWith(".")
                || new string[] { "CON", "PRN", "AUX", "NUL" }.Contains(name.ToUpperInvariant())
                || (name.Length == 4 && (name.Substring(0, 3).ToUpperInvariant() == "COM"
                    || name.Substring(0, 3).ToUpperInvariant() == "LPT") && char.IsDigit(name[3])))
            {
                MessageBox.Show("这个名字和系统/程序保留的名字撞了，换一个。", "名字不合法",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (name.IndexOfAny(new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                MessageBox.Show("名字里不能有 \\ / : * ? \" < > | 这些符号。", "名字不合法",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_accounts.Any(delegate (string n) { return n.ToLowerInvariant() == name.ToLowerInvariant(); }))
            {
                MessageBox.Show("已经有叫这个名字的账号了（大小写不同的也算同一个，文件夹会撞车串号）。", "已存在",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _accounts.Add(name);
            SaveAccounts();
            BuildBar();
            SwitchTo(name);
        }

        void DeleteCurrent()
        {
            if (_currentAccount == null) return;
            string cur = _currentAccount;
            if (_accounts.Count <= 1)
            {
                MessageBox.Show("至少要留一个账号。", "删不了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show("确定删除「" + cur + "」吗？它的登录和数据会被清掉。", "删除账号",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _accounts.Remove(cur);
            SaveAccounts();
            WebView2 v;
            if (_views.TryGetValue(cur, out v))
            {
                _views.Remove(cur);
                _host.Controls.Remove(v);
                v.Dispose();
            }
            _envs.Remove(cur);
            string folder = Path.Combine(App.DataRoot(), cur);
            Task.Run(delegate ()
            {
                Thread.Sleep(800);
                try { Directory.Delete(folder, true); } catch { }
            });
            BuildBar();
            SwitchTo(_accounts[0]);
        }

        static string Prompt(string title, string label)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.Width = 360;
                f.Height = 150;
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                Label l = new Label();
                l.Text = label;
                l.Left = 12; l.Top = 12; l.Width = 320;
                TextBox t = new TextBox();
                t.Left = 12; t.Top = 38; t.Width = 320;
                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.Left = 160; ok.Top = 75; ok.Width = 75;
                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Left = 245; cancel.Top = 75; cancel.Width = 75;
                f.Controls.Add(l);
                f.Controls.Add(t);
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }

        void RestoreGeometry()
        {
            try
            {
                string g = App.GetStr(_settings, "geometry", "");
                if (g.Length == 0) return;
                string[] p = g.Split(',');
                if (p.Length < 4) return;
                int x, y, w, h;
                if (int.TryParse(p[0], out x) && int.TryParse(p[1], out y)
                    && int.TryParse(p[2], out w) && int.TryParse(p[3], out h))
                {
                    Location = new Point(x, y);
                    Size = new Size(Math.Max(400, w), Math.Max(300, h));
                    if (p.Length >= 5 && p[4] == "max") WindowState = FormWindowState.Maximized;
                }
            }
            catch { }
        }

        void SaveGeometry()
        {
            try
            {
                string g;
                if (WindowState == FormWindowState.Maximized)
                    g = RestoreBounds.X + "," + RestoreBounds.Y + "," + RestoreBounds.Width + "," + RestoreBounds.Height + ",max";
                else if (WindowState == FormWindowState.Minimized)
                    g = RestoreBounds.X + "," + RestoreBounds.Y + "," + RestoreBounds.Width + "," + RestoreBounds.Height + ",normal";
                else
                    g = Location.X + "," + Location.Y + "," + Size.Width + "," + Size.Height + ",normal";
                App.Set(_settings, "geometry", g);
                App.SaveSettings(_settings);
            }
            catch { }
        }

        void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // 自动更新：每天首次启动时静默查一次；点“检查更新”按钮随时手动查
        bool _updateCheckRunning = false;

        void CheckUpdate(bool manual)
        {
            if (_updateCheckRunning) return;
            try
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                if (!manual && App.GetStr(_settings, "update_last_check", "") == today) return;
                App.Set(_settings, "update_last_check", today);
                App.SaveSettings(_settings);
            }
            catch { }
            _updateCheckRunning = true;
            System.Threading.Tasks.Task.Run(delegate ()
            {
                try
                {
                    JObject v = UpdateCheck.FetchVersion();
                    string ver = (string)v["version"] ?? "";
                    string notes = (string)v["notes"] ?? "";
                    if (ver.Length == 0) { if (manual) ShowUpdateMsg("没有取到更新信息。"); return; }
                    if (UpdateCheck.IsNewer(ver, App.Version))
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (MessageBox.Show(this, "发现新版本 " + ver + "（当前 " + App.Version + "）。\r\n" + notes
                                + "\r\n\r\n现在下载并更新吗？更新时会重新编译，约一两分钟，编好后自动重启。",
                                "发现新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                                DownloadAndInstallUpdate(ver);
                        }));
                    }
                    else if (manual)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            MessageBox.Show(this, "已经是最新版本（" + App.Version + "）。", "检查更新",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }));
                    }
                }
                catch (Exception ex)
                {
                    if (manual) ShowUpdateMsg("检查更新失败：" + ex.Message);
                }
                finally { _updateCheckRunning = false; }
            });
        }

        void ShowUpdateMsg(string msg)
        {
            try { BeginInvoke(new Action(delegate { MessageBox.Show(this, msg, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning); })); } catch { }
        }

        // 拉源码包 -> 补 res 文件 -> 本地 build.bat 编译 -> 更新器替换并重启
        void DownloadAndInstallUpdate(string ver)
        {
            ShowStatus("正在下载新版本 " + ver + " 的源码包，请稍候…", 0);
            System.Threading.Tasks.Task.Run(delegate ()
            {
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MuseUpdate");
                try
                {
                    if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
                    System.IO.Directory.CreateDirectory(tmp);
                    string zip = System.IO.Path.Combine(tmp, "source.zip");
                    UpdateCheck.DownloadFile(App.UpdateSourceZip, zip);
                    UpdateCheck.ExtractZip(zip, tmp);
                    string buildDir = null;
                    foreach (string d in System.IO.Directory.GetDirectories(tmp))
                    {
                        if (System.IO.File.Exists(System.IO.Path.Combine(d, "build.bat"))) { buildDir = d; break; }
                    }
                    if (buildDir == null) throw new Exception("更新包里没有找到 build.bat");
                    // 编译要内嵌 mihomo.exe / geoip.metadb：先从本机安装目录补一份
                    string resDst = System.IO.Path.Combine(buildDir, "res");
                    string resSrc = System.IO.Path.Combine(UpdateCheck.AppRoot(), "res");
                    if (System.IO.Directory.Exists(resSrc)) UpdateCheck.CopyDir(resSrc, resDst);
                    else
                    {
                        System.IO.Directory.CreateDirectory(resDst);
                        string core = App.CoreDir();
                        if (System.IO.File.Exists(System.IO.Path.Combine(core, "mihomo.exe")))
                            System.IO.File.Copy(System.IO.Path.Combine(core, "mihomo.exe"), System.IO.Path.Combine(resDst, "mihomo.exe"), true);
                        if (System.IO.File.Exists(System.IO.Path.Combine(core, "geoip.metadb")))
                            System.IO.File.Copy(System.IO.Path.Combine(core, "geoip.metadb"), System.IO.Path.Combine(resDst, "geoip.metadb"), true);
                    }
                    if (!System.IO.File.Exists(System.IO.Path.Combine(resDst, "mihomo.exe")))
                        throw new Exception("本机找不到 mihomo.exe（res/ 和 Muse数据/代理核心/ 里都没有），没法编译，先从完整安装包里拷一份 res 文件夹再更新");
                    // 编译还要 lib/ 里的 5 个 DLL：同样从本机安装目录补一份
                    string libSrc = System.IO.Path.Combine(UpdateCheck.AppRoot(), "lib");
                    string libDst = System.IO.Path.Combine(buildDir, "lib");
                    if (System.IO.Directory.Exists(libSrc)) UpdateCheck.CopyDir(libSrc, libDst);
                    BeginInvoke(new Action(delegate { ShowStatus("源码已下好，正在编译新版本，请稍候…", 0); }));
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c \"build.bat > build_stdout.log 2>&1 < nul\"");
                    psi.WorkingDirectory = buildDir;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    var p = System.Diagnostics.Process.Start(psi);
                    p.WaitForExit();
                    string exe = System.IO.Path.Combine(buildDir, "bin", "Muse.exe");
                    if (!System.IO.File.Exists(exe) || new System.IO.FileInfo(exe).Length < 10000000)
                    {
                        string log = "";
                        try { log = System.IO.File.ReadAllText(System.IO.Path.Combine(buildDir, "build.log")); } catch { }
                        if (log.Length > 600) log = log.Substring(0, 600) + "…";
                        throw new Exception("编译失败，新版本没有编出来，旧版本没动。\r\n" + log);
                    }
                    string bat = System.IO.Path.Combine(buildDir, "update.bat");
                    System.IO.File.WriteAllText(bat, UpdateCheck.UpdateBatText(), new System.Text.ASCIIEncoding());
                    BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bat,
                                "\"" + UpdateCheck.AppRoot() + "\" \"" + buildDir + "\"") { UseShellExecute = true, WorkingDirectory = buildDir });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(this, "启动更新程序失败：" + ex.Message, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        Application.Exit();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        ShowStatus("更新失败：" + ex.Message, 12000);
                        MessageBox.Show(this, "更新失败：" + ex.Message, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
            });
        }

        public void RestartApp()
        {
            try { ProxyCore.StopCore(); } catch { }
            try
            {
                Process.Start(Application.ExecutablePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("重启失败：" + ex.Message, "Muse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Application.Exit();
        }

        void Revive()
        {
            ShowStatus("网络已恢复可用，正在重启切换过去", 8000);
            RestartApp();
        }

        WebView2 CurrentView()
        {
            if (_currentAccount == null) return null;
            WebView2 v;
            _views.TryGetValue(_currentAccount, out v);
            return v;
        }

        // 供弹窗调用：把链接在当前账号窗口打开
        public void NavigateCurrent(string url)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(delegate { NavigateCurrent(url); })); return; }
            try
            {
                WebView2 v = CurrentView();
                if (v != null) v.Source = new Uri(url);
            }
            catch { }
        }

        // 供选线路对话框调用：切完节点后刷新顶栏线路显示
        public void OnNodePicked(string choice)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(delegate { OnNodePicked(choice); })); return; }
            SetLine(choice);
            _fail.N = 0;
            _fail.Alerted = false;
        }

        // ---------- WebView2 视图 ----------
        public void SwitchTo(string name)
        {
            if (!_views.ContainsKey(name)) CreateViewAsync(name);
            foreach (KeyValuePair<string, WebView2> kv in _views)
                kv.Value.Visible = kv.Key == name;
            _currentAccount = name;
            foreach (KeyValuePair<string, Button> kv in _accButtons)
            {
                kv.Value.Font = new Font(kv.Value.Font, kv.Key == name ? FontStyle.Bold : FontStyle.Regular);
                kv.Value.BackColor = kv.Key == name ? Color.LightSteelBlue : SystemColors.Control;
            }
            Text = "Muse - " + name;
        }

        async void CreateViewAsync(string name)
        {
            string folder = Path.Combine(App.DataRoot(), name);
            Directory.CreateDirectory(folder);
            var view = new WebView2();
            view.Dock = DockStyle.Fill;
            view.Visible = false;
            _host.Controls.Add(view);
            _views[name] = view;
            var ls = new LoadState();
            ls.Account = name;
            _loadStates[view] = ls;
            try
            {
                var opts = new CoreWebView2EnvironmentOptions(_browserArgs);
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, folder, opts);
                _envs[name] = env;
                await view.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show("浏览器内核初始化失败（需要安装 WebView2 Runtime，一般随 Edge 自带）：\r\n" + ex.Message,
                    "Muse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            CoreWebView2 core = view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            WireView(view, core, ls);
            view.Visible = name == _currentAccount;
            core.Navigate("https://muse.ai");
        }

        void WireView(WebView2 view, CoreWebView2 core, LoadState ls)
        {
            core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs e)
            {
                ls.Since = DateTime.Now;
            };
            core.NavigationCompleted += delegate (object s, CoreWebView2NavigationCompletedEventArgs e)
            {
                ls.Since = DateTime.MinValue;
                try
                {
                    Uri u = view.Source;
                    if (u == null) return;
                    bool isLogin = u.AbsolutePath.Contains("/login") || u.AbsolutePath.Contains("/auth")
                        || u.AbsolutePath.Contains("/sign-in") || u.AbsolutePath.Contains("/signin");
                    if (e.IsSuccess && u.Host.EndsWith("muse.ai") && !isLogin)
                    {
                        ls.WasIn = true;
                        ls.DropNoted = false;
                    }
                    if (isLogin && ls.WasIn && !ls.DropNoted)
                    {
                        ls.DropNoted = true;
                        ShowStatus("账号「" + ls.Account + "」的登录掉了（可能在别处登了同一个号被顶，或登录过期），切过去重新登录即可", 20000);
                    }
                }
                catch { }
                if (e.IsSuccess) { ls.Retries = 0; return; }
                if (ls.Retries < 2)
                {
                    ls.Retries++;
                    var t = new System.Windows.Forms.Timer();
                    t.Interval = 3000;
                    t.Tick += delegate { t.Stop(); try { view.Reload(); } catch { } };
                    t.Start();
                    return;
                }
                ls.Retries = 0;
                if (App.CoreInUse)
                    ShowStatus("页面多次加载失败，已停止自动重试，可点刷新再试", 12000);
                else if (App.ProxyBlocked)
                    ShowStatus("页面加载失败：当前没有可用代理，先把代理设置弄好", 12000);
                else if (!_loadPromptDone && CurrentView() == view)
                {
                    _loadPromptDone = true;
                    if (MessageBox.Show("多次加载失败，可能是代理失效了。要重启程序重新检测吗？", "页面加载失败",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        RestartApp();
                }
            };
            core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs e)
            {
                try
                {
                    var popup = new PopupForm(this, _envs[ls.Account], e.Uri);
                    popup.Show(this);
                    e.NewWindow = popup.View.CoreWebView2;
                    e.Handled = true;
                }
                catch { e.Handled = false; }
            };
            core.DownloadStarting += delegate (object s, CoreWebView2DownloadStartingEventArgs e)
            {
                try
                {
                    string uri = e.DownloadOperation.Uri;
                    if (uri.StartsWith("http://") || uri.StartsWith("https://"))
                    {
                        e.Cancel = true;
                        _recentDownloads.Add(uri);
                        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
                        ShowStatus("已拉起本地浏览器打开", 15000);
                    }
                    else if (uri.StartsWith("blob:") || uri.StartsWith("data:"))
                    {
                        string fname = "";
                        try
                        {
                            string p = new Uri(uri).AbsolutePath;
                            fname = Path.GetFileName(p);
                        }
                        catch { }
                        if (fname.Length == 0) fname = "下载_" + DateTime.Now.ToString("HHmmss");
                        string dlDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                        Directory.CreateDirectory(dlDir);
                        using (var sfd = new SaveFileDialog())
                        {
                            sfd.Title = "保存文件";
                            sfd.FileName = Path.Combine(dlDir, fname);
                            if (sfd.ShowDialog(this) != DialogResult.OK)
                            {
                                e.Cancel = true;
                                ShowStatus("已取消保存", 8000);
                                return;
                            }
                            e.ResultFilePath = sfd.FileName;
                        }
                        TrackDownload(e.DownloadOperation, Path.GetFileName(e.ResultFilePath));
                    }
                    else
                    {
                        e.Cancel = true;
                        ShowStatus("账号「" + ls.Account + "」这个文件是网页内生成的，没法转交本地浏览器", 12000);
                    }
                }
                catch (Exception ex) { App.LogException(ex); }
            };
            core.PermissionRequested += delegate (object s, CoreWebView2PermissionRequestedEventArgs e)
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                    e.State = CoreWebView2PermissionState.Allow;
            };
            var crashHits = new List<DateTime>();
            core.ProcessFailed += delegate (object s, CoreWebView2ProcessFailedEventArgs e)
            {
                try
                {
                    DateTime now = DateTime.Now;
                    crashHits.RemoveAll(delegate (DateTime t) { return (now - t).TotalSeconds >= 60; });
                    crashHits.Add(now);
                    if (crashHits.Count <= 3)
                    {
                        var t = new System.Windows.Forms.Timer();
                        t.Interval = 1000;
                        t.Tick += delegate { t.Stop(); try { view.Reload(); } catch { } };
                        t.Start();
                    }
                }
                catch { }
            };
            core.ContextMenuRequested += delegate (object s, CoreWebView2ContextMenuRequestedEventArgs e)
            {
                try
                {
                    var menu = new ContextMenuStrip();
                    CoreWebView2ContextMenuTarget target = e.ContextMenuTarget;
                    bool hasLink = target != null && target.LinkUri != null && target.LinkUri.Length > 0;
                    bool isImage = target != null && target.Kind == CoreWebView2ContextMenuTargetKind.Image;
                    bool hasSel = target != null && target.HasSelection;
                    bool editable = target != null && target.IsEditable;
                    if (hasLink)
                    {
                        string link = target.LinkUri;
                        menu.Items.Add("打开链接", null, delegate { view.Source = new Uri(link); });
                        menu.Items.Add("复制链接地址", null, delegate { Clipboard.SetText(link); });
                        menu.Items.Add(new ToolStripSeparator());
                    }
                    if (isImage)
                    {
                        string img = target.LinkUri ?? "";
                        menu.Items.Add("复制图片地址", null, delegate { if (img.Length > 0) Clipboard.SetText(img); });
                        menu.Items.Add(new ToolStripSeparator());
                    }
                    if (editable)
                    {
                        menu.Items.Add("剪切", null, delegate { core.ExecuteScriptAsync("document.execCommand('cut')"); });
                        menu.Items.Add("复制", null, delegate { core.ExecuteScriptAsync("document.execCommand('copy')"); });
                        menu.Items.Add("粘贴", null, delegate { core.ExecuteScriptAsync("document.execCommand('paste')"); });
                        menu.Items.Add(new ToolStripSeparator());
                        menu.Items.Add("全选", null, delegate { core.ExecuteScriptAsync("document.execCommand('selectAll')"); });
                    }
                    else if (hasSel)
                    {
                        menu.Items.Add("复制", null, delegate { core.ExecuteScriptAsync("document.execCommand('copy')"); });
                        menu.Items.Add("全选", null, delegate { core.ExecuteScriptAsync("document.execCommand('selectAll')"); });
                    }
                    else
                    {
                        menu.Items.Add("复制", null, delegate { core.ExecuteScriptAsync("document.execCommand('copy')"); });
                        menu.Items.Add("粘贴", null, delegate { core.ExecuteScriptAsync("document.execCommand('paste')"); });
                        menu.Items.Add("全选", null, delegate { core.ExecuteScriptAsync("document.execCommand('selectAll')"); });
                    }
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("后退", null, delegate { if (core.CanGoBack) core.GoBack(); });
                    menu.Items.Add("前进", null, delegate { if (core.CanGoForward) core.GoForward(); });
                    menu.Items.Add("刷新", null, delegate { view.Reload(); });
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("查看源代码", null, delegate { core.Navigate("view-source:" + view.Source); });
                    menu.Items.Add("检查", null, delegate { core.OpenDevToolsWindow(); });
                    e.Handled = true;
                    menu.Show(Cursor.Position);
                }
                catch { }
            };
        }

        // ---------- 下载进度 ----------
        readonly Dictionary<CoreWebView2DownloadOperation, string> _dlActive = new Dictionary<CoreWebView2DownloadOperation, string>();
        System.Windows.Forms.Timer _dlHideTimer;

        void TrackDownload(CoreWebView2DownloadOperation op, string name)
        {
            if (InvokeRequired) { BeginInvoke(new Action(delegate { TrackDownload(op, name); })); return; }
            if (_dlHideTimer == null)
            {
                _dlHideTimer = new System.Windows.Forms.Timer();
                _dlHideTimer.Interval = 5000;
                _dlHideTimer.Tick += delegate
                {
                    _dlHideTimer.Stop();
                    if (_dlActive.Count == 0) { _dlLabel.Visible = false; _dlBar.Visible = false; }
                };
            }
            _dlHideTimer.Stop();
            _dlActive[op] = name;
            op.BytesReceivedChanged += delegate { DlRefresh(); };
            op.StateChanged += delegate
            {
                try
                {
                    if (op.State == CoreWebView2DownloadState.Completed || op.State == CoreWebView2DownloadState.Interrupted)
                    {
                        bool done = op.State == CoreWebView2DownloadState.Completed;
                        _dlActive.Remove(op);
                        if (_dlActive.Count > 0) DlRefresh();
                        else if (done)
                        {
                            _dlBar.Value = 100;
                            _dlLabel.Text = "下载完成：" + name;
                            _dlLabel.Visible = true;
                            _dlBar.Visible = true;
                            _dlHideTimer.Interval = 5000;
                            _dlHideTimer.Start();
                        }
                        else
                        {
                            _dlLabel.Text = "下载失败：" + name;
                            _dlLabel.Visible = true;
                            _dlBar.Visible = true;
                            _dlHideTimer.Interval = 6000;
                            _dlHideTimer.Start();
                        }
                    }
                }
                catch { }
            };
            DlRefresh();
        }

        void DlRefresh()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(delegate { DlRefresh(); })); return; }
            try
            {
                if (_dlActive.Count == 0) return;
                CoreWebView2DownloadOperation op = _dlActive.Keys.Last();
                string nm = _dlActive[op];
                string extra = _dlActive.Count > 1 ? "（共 " + _dlActive.Count + " 个）" : "";
                double totalBytes = Convert.ToDouble(op.TotalBytesToReceive);
                if (totalBytes > 0)
                {
                    int pct = (int)(Convert.ToDouble(op.BytesReceived) * 100.0 / totalBytes);
                    _dlBar.Style = ProgressBarStyle.Continuous;
                    _dlBar.Value = Math.Min(100, pct);
                    _dlLabel.Text = "下载中：" + nm + extra + " " + pct + "%";
                }
                else
                {
                    _dlBar.Style = ProgressBarStyle.Marquee;
                    _dlLabel.Text = "下载中：" + nm + extra;
                }
                _dlLabel.Visible = true;
                _dlBar.Visible = true;
            }
            catch { }
        }

        // ---------- 定时器：失效切换 / 线路巡检 / 守卫 / 空间 / 缓存 / 流量 ----------
        void CheckNodes()
        {
            if (_fail.Busy) return;
            if (!App.GetBool(_settings, "auto_failover", true)) return;
            if (!App.CoreInUse || !ProxyCore.CoreAlive()) return;
            _fail.Busy = true;
            Task.Run(delegate ()
            {
                try
                {
                    Tuple<string, JObject> g = Regions.MainSelectorGroup();
                    if (g.Item1 == null) return;
                    JToken nowTok = g.Item2["now"];
                    string current = nowTok == null ? "" : (string)nowTok;
                    if (current.Length == 0) return;
                    int? delay = Regions.NodeDelay(current);
                    if (delay != null)
                    {
                        _fail.N = 0;
                        _fail.Since = DateTime.MinValue;
                        _fail.Alerted = false;
                        _fail.NoRegNoted = false;
                        SetLine(current + " " + delay + "ms");
                        return;
                    }
                    _fail.N++;
                    DateTime nowT = DateTime.Now;
                    if (_fail.Since == DateTime.MinValue)
                    {
                        _fail.Since = nowT;
                        ShowStatus("线路「" + current + "」测速失败，观察中（断网满 10 分钟才自动切换）", 15000);
                        return;
                    }
                    if ((nowT - _fail.Since).TotalSeconds < 600) return;
                    string region = App.GetStr(_settings, "locked_region", "");
                    if (region.Length == 0)
                    {
                        if (!_fail.NoRegNoted)
                        {
                            _fail.NoRegNoted = true;
                            ShowStatus("线路「" + current + "」已断网超 10 分钟，未手动锁定地区，不自动切换", 15000);
                        }
                        return;
                    }
                    Tuple<string, int> best = null;
                    string sticky = App.GetStr(_settings, "sticky_node", "");
                    if (sticky.Length > 0 && sticky != current && !Regions.IsJunkNode(sticky)
                        && Regions.RegionOf(sticky) == region)
                    {
                        int? d = Regions.NodeDelay(sticky);
                        if (d != null) best = Tuple.Create(sticky, d.Value);
                    }
                    if (best == null)
                    {
                        JArray all = g.Item2["all"] as JArray;
                        if (all != null)
                        {
                            foreach (JToken c in all)
                            {
                                string cand = (string)c;
                                if (cand == current || Regions.IsJunkNode(cand)) continue;
                                if (Regions.RegionOf(cand) != region) continue;
                                int? d = Regions.NodeDelay(cand);
                                if (d != null && (best == null || d.Value < best.Item2))
                                    best = Tuple.Create(cand, d.Value);
                            }
                        }
                    }
                    if (best == null)
                    {
                        ShowStatus("线路「" + current + "」已失效，锁定地区内无可用线路", 15000);
                        if (!_fail.Alerted)
                        {
                            _fail.Alerted = true;
                            if (InvokeRequired)
                                BeginInvoke(new Action(delegate
                                {
                                    MessageBox.Show(this, "锁定的「" + region + "」地区线路已全部失效，自动切换无可用线路。\r\n当前保持在：" + current + "\r\n等线路恢复会自动测速确认，或手动到「代理设置 → 选择线路」换区。",
                                        "线路全部失效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                }));
                        }
                        return;
                    }
                    ProxyCore.CoreApi("/proxies/" + Uri.EscapeDataString(g.Item1), "PUT",
                        App.NewNameObj(best.Item1));
                    _fail.N = 0;
                    _fail.Since = DateTime.MinValue;
                    SetLine(best.Item1 + " " + best.Item2 + "ms");
                    ShowStatus("线路已断网超 10 分钟，已自动切换到「" + best.Item1 + "」", 15000);
                }
                catch { }
                finally { _fail.Busy = false; }
            });
        }

        void PollLine()
        {
            if (_lineState.Busy || !App.CoreInUse) return;
            _lineState.Busy = true;
            Task.Run(delegate ()
            {
                try
                {
                    if (!ProxyCore.CoreAlive()) return;
                    string gname = _lineState.Group;
                    JObject ginfo = null;
                    if (gname != null && gname.Length > 0)
                    {
                        try
                        {
                            JToken t = ProxyCore.CoreApi("/proxies/" + Uri.EscapeDataString(gname));
                            ginfo = t as JObject;
                        }
                        catch { ginfo = null; }
                    }
                    if (ginfo == null || (string)ginfo["type"] != "Selector")
                    {
                        Tuple<string, JObject> g = Regions.MainSelectorGroup();
                        if (g.Item1 == null) return;
                        gname = g.Item1;
                        ginfo = g.Item2;
                        _lineState.Group = gname;
                    }
                    JToken nowTok = ginfo["now"];
                    string now = nowTok == null ? "" : (string)nowTok;
                    if (now.Length == 0) return;
                    string locked = App.GetStr(_settings, "locked_region", "");
                    if (locked.Length > 0 && App.GetBool(_settings, "auto_failover", true))
                    {
                        string curRegion = Regions.RegionOf(now);
                        if (curRegion != null && curRegion != locked)
                        {
                            _lineState.Mis++;
                            if (_lineState.Mis >= 2)
                            {
                                _lineState.Mis = 0;
                                Tuple<string, int> best = null;
                                string sticky = App.GetStr(_settings, "sticky_node", "");
                                if (sticky.Length > 0 && sticky != now && !Regions.IsJunkNode(sticky)
                                    && Regions.RegionOf(sticky) == locked)
                                {
                                    int? d = Regions.NodeDelay(sticky);
                                    if (d != null) best = Tuple.Create(sticky, d.Value);
                                }
                                if (best == null)
                                {
                                    JArray all = ginfo["all"] as JArray;
                                    if (all != null)
                                    {
                                        foreach (JToken c in all)
                                        {
                                            string cand = (string)c;
                                            if (cand == now || Regions.IsJunkNode(cand)) continue;
                                            if (Regions.RegionOf(cand) != locked) continue;
                                            int? d = Regions.NodeDelay(cand);
                                            if (d != null && (best == null || d.Value < best.Item2))
                                                best = Tuple.Create(cand, d.Value);
                                        }
                                    }
                                }
                                if (best != null)
                                {
                                    ProxyCore.CoreApi("/proxies/" + Uri.EscapeDataString(gname), "PUT",
                                        App.NewNameObj(best.Item1));
                                    _lineState.Name = best.Item1;
                                    _lineState.DelayT = DateTime.Now;
                                    SetLine(best.Item1 + " " + best.Item2 + "ms");
                                    ShowStatus("线路跑出锁定地区，已切回「" + locked + "」：" + best.Item1, 15000);
                                    return;
                                }
                                ShowStatus("锁定地区「" + locked + "」无可用线路，保持当前线路不跨区", 15000);
                            }
                        }
                        else _lineState.Mis = 0;
                    }
                    DateTime nowTime = DateTime.Now;
                    if (now != _lineState.Name)
                    {
                        _lineState.Name = now;
                        int? d = Regions.NodeDelay(now);
                        _lineState.DelayT = nowTime;
                        SetLine(d != null ? now + " " + d + "ms" : now);
                    }
                    else if ((nowTime - _lineState.DelayT).TotalSeconds > 30)
                    {
                        int? d = Regions.NodeDelay(now);
                        _lineState.DelayT = nowTime;
                        if (d != null) SetLine(now + " " + d + "ms");
                    }
                }
                catch { }
                finally { _lineState.Busy = false; }
            });
        }

        void GuardTick()
        {
            if (App.ProxyBlocked && App.GetBool(_settings, "use_local_proxy", false)
                && !_guard.AutoSwitched && !_guard.ReviveBusy)
            {
                _guard.ReviveBusy = true;
                Task.Run(delegate ()
                {
                    try
                    {
                        string cand = ProxyCore.AutoProxyUrl();
                        if ((cand.Length > 0 && ProxyCore.ProbeWorks(cand)) || ProxyCore.ProbeWorks(""))
                        {
                            _guard.AutoSwitched = true;
                            BeginInvoke(new Action(delegate { Revive(); }));
                        }
                    }
                    catch { }
                    finally { _guard.ReviveBusy = false; }
                });
            }
            if (App.RefreshDone && App.RefreshOk == false && !_guard.RefreshNoted)
            {
                _guard.RefreshNoted = true;
                ShowStatus("订阅自动更新失败，已继续用旧配置；可到「代理设置」手动更新", 20000);
            }
            DateTime now = DateTime.Now;
            foreach (KeyValuePair<WebView2, LoadState> kv in _loadStates)
            {
                LoadState st = kv.Value;
                if (st.Since != DateTime.MinValue && (now - st.Since).TotalSeconds > 45 && st.Retries < 2)
                {
                    st.Retries++;
                    st.Since = DateTime.MinValue;
                    try { kv.Key.Reload(); } catch { }
                }
            }
            if (_guard.Busy || !App.GetBool(_settings, "core_enabled", true)) return;
            _guard.Busy = true;
            Task.Run(delegate ()
            {
                try
                {
                    if (!File.Exists(Path.Combine(App.CoreDir(), "config.yaml"))) return;
                    if (ProxyCore.CoreAlive()) { _guard.Fails = 0; return; }
                    if (_guard.Fails >= 3) return;
                    if (ProxyCore.StartCore())
                    {
                        _guard.Fails = 0;
                        ShowStatus("核心曾中断，已自动重启", 15000);
                        BeginInvoke(new Action(delegate
                        {
                            foreach (WebView2 v in _views.Values)
                            {
                                try { v.Reload(); } catch { }
                            }
                        }));
                    }
                    else
                    {
                        _guard.Fails++;
                        if (_guard.Fails == 3)
                            ShowStatus("核心多次重启失败，请看 Muse数据\\代理核心\\core.log", 15000);
                    }
                }
                catch { }
                finally { _guard.Busy = false; }
            });
        }

        void SpaceCheck()
        {
            try
            {
                DriveInfo d = new DriveInfo(Path.GetPathRoot(App.BaseDir()));
                if (d.AvailableFreeSpace < 500L * 1024 * 1024)
                    ShowStatus("磁盘空间不足（剩 " + (d.AvailableFreeSpace / 1024 / 1024) + " MB），账号数据可能存不下，先清点空间", 20000);
            }
            catch { }
        }

        // WebView2 运行时清不了“仅 HTTP 缓存”（清全部会把登录也清掉），所以超限只提示并记个标记，
        // 下次启动前直接删缓存文件夹（与 py 启动时删文件夹的行为一致）。
        void CacheCheck()
        {
            Task.Run(delegate ()
            {
                foreach (string n in _accounts)
                {
                    string cdir = Path.Combine(App.DataRoot(), n, "缓存");
                    try
                    {
                        if (Directory.Exists(cdir) && App.DirSize(cdir) > App.CacheLimit)
                        {
                            try { File.WriteAllText(Path.Combine(App.DataRoot(), n, "清缓存.txt"), ""); } catch { }
                            ShowStatus("账号「" + n + "」缓存超 1.5 GB，已自动清理（不影响登录）", 12000);
                        }
                    }
                    catch { }
                }
            });
        }

        void TrafficWorker()
        {
            while (!IsDisposed)
            {
                try
                {
                    if (!ProxyCore.CoreAlive()) { Thread.Sleep(3000); continue; }
                    using (var c = new HttpClient())
                    {
                        c.Timeout = TimeSpan.FromSeconds(15);
                        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + App.CoreSecret);
                        using (var stream = c.GetStreamAsync("http://127.0.0.1:" + App.CtrlPort + "/traffic").GetAwaiter().GetResult())
                        using (var rd = new StreamReader(stream, Encoding.UTF8))
                        {
                            string line;
                            while (!IsDisposed && (line = rd.ReadLine()) != null)
                            {
                                try
                                {
                                    JObject d = JObject.Parse(line);
                                    long down = d["down"] == null ? 0 : (long)d["down"];
                                    long up = d["up"] == null ? 0 : (long)d["up"];
                                    string text = "↓" + App.FmtRate(down).Replace(" ", "") + " ↑" + App.FmtRate(up).Replace(" ", "");
                                    BeginInvoke(new Action(delegate { _speedLabel.Text = text; }));
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { Thread.Sleep(3000); }
            }
        }

        void PrewarmNext(int idx)
        {
            if (idx >= _accounts.Count) return;
            string name = _accounts[idx];
            if (!_views.ContainsKey(name)) CreateViewAsync(name);
            var t = new System.Windows.Forms.Timer();
            t.Interval = 2500;
            t.Tick += delegate { t.Stop(); PrewarmNext(idx + 1); };
            t.Start();
        }
    }
}
