using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace Muse
{
    // 代理设置对话框（对应 py 的 open_proxy_dialog）
    public class ProxyDialog : Form
    {
        readonly MainForm _main;
        readonly JObject _settings;
        CheckBox _coreChk, _failChk, _localChk;
        Label _statusLabel, _hintLabel;
        ListBox _subsList;
        TextBox _nameEdit, _urlEdit;
        Button _updBtn, _nodeBtn, _saveBtn;
        readonly List<JObject> _subEntries = new List<JObject>();

        public ProxyDialog(MainForm main, JObject settings)
        {
            _main = main;
            _settings = settings;
            Text = "代理设置";
            Width = 560;
            Height = 520;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Font = new Font("微软雅黑", 9f);

            var scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            Controls.Add(scroll);

            var t = new TableLayoutPanel();
            t.Dock = DockStyle.Top;
            t.AutoSize = true;
            t.ColumnCount = 1;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Padding = new Padding(12, 10, 12, 10);
            scroll.Controls.Add(t);

            int row = 0;
            Action<Control, int> addRow = delegate (Control c, int gap)
            {
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                c.Margin = new Padding(0, 0, 0, gap);
                t.Controls.Add(c, 0, row);
                row++;
            };

            Label cur = new Label();
            cur.Text = "当前生效：" + App.LastProxyDesc;
            cur.AutoSize = true;
            cur.MaximumSize = new Size(512, 0);
            addRow(cur, 6);

            _statusLabel = new Label();
            _statusLabel.Text = "核心状态：" + App.CoreState;
            _statusLabel.AutoSize = true;
            _statusLabel.MaximumSize = new Size(512, 0);
            addRow(_statusLabel, 8);

            _coreChk = new CheckBox();
            _coreChk.Text = "启用内置代理核心（不用再单独开 Clash Meta）";
            _coreChk.AutoSize = true;
            _coreChk.Checked = App.GetBool(_settings, "core_enabled", true);
            addRow(_coreChk, 2);

            _failChk = new CheckBox();
            _failChk.Text = "线路失效自动切换（只在手动锁定的地区内切，绝不跨区、绝不自选地区）";
            _failChk.AutoSize = true;
            _failChk.Checked = App.GetBool(_settings, "auto_failover", true);
            addRow(_failChk, 2);

            _localChk = new CheckBox();
            _localChk.Text = "启用本地代理（自动识别系统代理/本机端口，内置代理用不了时兜底）";
            _localChk.AutoSize = true;
            _localChk.Checked = App.GetBool(_settings, "use_local_proxy", false);
            addRow(_localChk, 8);

            _hintLabel = new Label();
            _hintLabel.AutoSize = true;
            _hintLabel.MaximumSize = new Size(512, 0);
            addRow(_hintLabel, 4);
            _coreChk.CheckedChanged += delegate { _hintLabel.Text = "设置已改动，点「保存并重启」后生效"; };
            _failChk.CheckedChanged += delegate { _hintLabel.Text = "设置已改动，点「保存并重启」后生效"; };
            _localChk.CheckedChanged += delegate { _hintLabel.Text = "设置已改动，点「保存并重启」后生效"; };

            string locked = App.GetStr(_settings, "locked_region", "");
            Label lockLbl = new Label();
            lockLbl.Text = "当前锁定地区：" + (locked.Length > 0 ? locked : "未锁定（按当前线路地区）")
                + "；在「选择线路」里手动选一次即锁定其地区";
            lockLbl.AutoSize = true;
            lockLbl.MaximumSize = new Size(512, 0);
            addRow(lockLbl, 6);

            Label subLbl = new Label();
            subLbl.Text = "订阅链接（可加多个，所有订阅的节点会合并到一起用）：";
            subLbl.AutoSize = true;
            addRow(subLbl, 4);

            _subsList = new ListBox();
            _subsList.Dock = DockStyle.Fill;
            _subsList.Margin = new Padding(0, 0, 0, 8);
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            t.Controls.Add(_subsList, 0, row);
            row++;
            foreach (JObject e in App.GetSubUrls(_settings)) _subEntries.Add((JObject)e.DeepClone());
            RebuildSubs(0);

            var srow = new FlowLayoutPanel();
            srow.AutoSize = true;
            srow.WrapContents = false;
            _nameEdit = new TextBox();
            _nameEdit.Width = 90;
            SetCue(_nameEdit, "名称（可空）");
            _urlEdit = new TextBox();
            _urlEdit.Width = 250;
            SetCue(_urlEdit, "粘贴订阅链接");
            Button addBtn = new Button();
            addBtn.Text = "添加订阅";
            addBtn.AutoSize = true;
            addBtn.Click += delegate { DoAddSub(); };
            Button delBtn = new Button();
            delBtn.Text = "删除选中";
            delBtn.AutoSize = true;
            delBtn.Click += delegate { DoDelSub(); };
            srow.Controls.Add(_nameEdit);
            srow.Controls.Add(_urlEdit);
            srow.Controls.Add(addBtn);
            srow.Controls.Add(delBtn);
            addRow(srow, 6);

            var row0 = new FlowLayoutPanel();
            row0.AutoSize = true;
            row0.WrapContents = false;
            _updBtn = new Button();
            _updBtn.Text = "保存订阅并更新";
            _updBtn.AutoSize = true;
            _updBtn.Click += delegate { DoUpdate(); };
            _nodeBtn = new Button();
            _nodeBtn.Text = "选择线路";
            _nodeBtn.AutoSize = true;
            _nodeBtn.Click += delegate { new NodePickerDialog(_main, _settings).ShowDialog(this); };
            row0.Controls.Add(_updBtn);
            row0.Controls.Add(_nodeBtn);
            addRow(row0, 6);

            var row2 = new FlowLayoutPanel();
            row2.AutoSize = true;
            row2.WrapContents = false;
            _saveBtn = new Button();
            _saveBtn.Text = "保存并重启";
            _saveBtn.AutoSize = true;
            _saveBtn.Click += delegate { DoSave(); };
            Button cancelBtn = new Button();
            cancelBtn.Text = "取消";
            cancelBtn.AutoSize = true;
            cancelBtn.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            row2.Controls.Add(_saveBtn);
            row2.Controls.Add(cancelBtn);
            addRow(row2, 0);
        }


        // .NET Framework 4.8 的 TextBox 没有 PlaceholderText，用 cue banner 实现
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static void SetCue(TextBox tb, string text)
        {
            try { SendMessage(tb.Handle, 0x1501, (IntPtr)1, text); } catch { }
        }

        void RebuildSubs(int? selectRow)
        {
            _subsList.Items.Clear();
            foreach (JObject e in _subEntries)
            {
                string u = (string)e["url"] ?? "";
                string shown = u.Length <= 64 ? u : u.Substring(0, 64) + "…";
                string name = (string)e["name"] ?? "订阅";
                _subsList.Items.Add(name + " — " + shown);
            }
            if (selectRow != null && _subsList.Items.Count > 0)
                _subsList.SelectedIndex = Math.Min(selectRow.Value, _subsList.Items.Count - 1);
        }

        void DoAddSub()
        {
            string url = _urlEdit.Text.Trim();
            if (url.Length == 0)
            {
                MessageBox.Show(this, "先把订阅链接粘贴进来。", "没填链接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            foreach (JObject e in _subEntries)
            {
                if ((string)e["url"] == url)
                {
                    MessageBox.Show(this, "这个订阅已经在列表里了。", "已存在", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            string name = _nameEdit.Text.Trim();
            if (name.Length == 0) name = "订阅" + (_subEntries.Count + 1);
            JObject ne = new JObject(); ne["name"] = name; ne["url"] = url; _subEntries.Add(ne);
            _nameEdit.Clear();
            _urlEdit.Clear();
            RebuildSubs(_subEntries.Count - 1);
        }

        void DoDelSub()
        {
            int row = _subsList.SelectedIndex;
            if (row < 0 || row >= _subEntries.Count)
            {
                MessageBox.Show(this, "先在列表里选中要删的订阅。", "没选中", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _subEntries.RemoveAt(row);
            RebuildSubs(0);
        }

        void DoUpdate()
        {
            if (_urlEdit.Text.Trim().Length > 0) DoAddSub();
            var urls = new List<string>();
            foreach (JObject e in _subEntries)
            {
                string u = (string)e["url"];
                if (!string.IsNullOrEmpty(u)) urls.Add(u);
            }
            if (urls.Count == 0)
            {
                MessageBox.Show(this, "先添加至少一个订阅链接。", "没填订阅", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _updBtn.Enabled = false;
            _nodeBtn.Enabled = false;
            _statusLabel.Text = "正在下载订阅并启动核心，请稍候…";
            bool coreOn = _coreChk.Checked;
            bool failOn = _failChk.Checked;
            bool localOn = _localChk.Checked;
            Task.Run(delegate ()
            {
                try
                {
                    BeginInvoke(new Action(delegate { _statusLabel.Text = "正在下载订阅，请稍候…"; }));
                    Tuple<bool, string> r = ProxyCore.DownloadSubscriptions(urls);
                    bool ok = r.Item1;
                    string msg = r.Item2;
                    // 校验超时不等于配置错：可能是核心在等网络或配置太大，给用户一个“跳过校验直接启用”的选择
                    if (!ok && ProxyCore.LastTimeoutConfig != null)
                    {
                        bool skip = false;
                        try
                        {
                            skip = (bool)Invoke(new Func<bool>(delegate
                            {
                                return MessageBox.Show(this, "新配置校验超时（60 秒没出结果），可能是核心在等网络，或配置太大。\r\n\r\n要跳过校验直接启用新配置吗？核心启动时会自己再验一次，配置真有问题核心起不来，旧配置已备份为 config.yaml.bak。\r\n（详情见 代理核心/订阅校验.log）",
                                    "校验超时", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                            }));
                        }
                        catch { }
                        if (skip && ProxyCore.InstallKeptConfig())
                        {
                            ok = true;
                            msg = "已跳过校验启用新配置，核心正在启动验证。";
                        }
                    }
                    App.Set(_settings, "core_enabled", coreOn);
                    App.Set(_settings, "auto_failover", failOn);
                    App.Set(_settings, "use_local_proxy", localOn);
                    var arr = new JArray();
                    foreach (JObject e in _subEntries) arr.Add((JObject)e.DeepClone());
                    _settings["sub_urls"] = arr;
                    App.Set(_settings, "sub_url", urls[0]);
                    App.SaveSettings(_settings);
                    if (ok)
                    {
                        string cfg = System.IO.Path.Combine(App.CoreDir(), "config.yaml");
                        if (ProxyCore.CoreAlive())
                        {
                            bool reloaded = false;
                            try
                            {
                                ProxyCore.CoreApi("/configs", "PUT", App.NewPathObj(cfg));
                                reloaded = true;
                            }
                            catch { reloaded = ProxyCore.CoreAlive(); }
                            if (reloaded) msg += "。新配置已热重载，立即生效，不用重启程序。";
                            else
                            {
                                ProxyCore.StartCore(90);
                                msg += "。核心已尝试启动，重启程序后浏览器正式走核心。";
                            }
                        }
                        else
                        {
                            BeginInvoke(new Action(delegate { _statusLabel.Text = "订阅已更新，正在启动核心，请稍候…"; }));
                            ProxyCore.StartCore(90);
                            msg += "。核心已尝试启动，重启程序后浏览器正式走核心。";
                        }
                    }
                    string state = App.CoreState;
                    BeginInvoke(new Action(delegate
                    {
                        _updBtn.Enabled = true;
                        _nodeBtn.Enabled = true;
                        _statusLabel.Text = "核心状态：" + (state.Length > 240 ? state.Substring(0, 240) : state);
                        MessageBox.Show(this, msg, "更新结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        _updBtn.Enabled = true;
                        _nodeBtn.Enabled = true;
                        MessageBox.Show(this, "更新出错：" + ex.Message, "更新结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
            });
        }

        void DoSave()
        {
            App.Set(_settings, "core_enabled", _coreChk.Checked);
            App.Set(_settings, "auto_failover", _failChk.Checked);
            App.Set(_settings, "use_local_proxy", _localChk.Checked);
            if (_urlEdit.Text.Trim().Length > 0) DoAddSub();
            var arr = new JArray();
            foreach (JObject e in _subEntries) arr.Add((JObject)e.DeepClone());
            _settings["sub_urls"] = arr;
            if (_subEntries.Count > 0) App.Set(_settings, "sub_url", (string)_subEntries[0]["url"]);
            App.SaveSettings(_settings);
            DialogResult = DialogResult.OK;
            Close();
            _main.RestartApp();
        }
    }
}
