using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace Muse
{
    // 选择线路对话框（对应 py 的 pick_node）：按地区归类、地区内按延迟从快到慢
    public class NodePickerDialog : Form
    {
        readonly MainForm _main;
        readonly JObject _settings;
        ListView _list;
        TextBox _search;
        Label _title;
        string _gname = "";
        readonly List<Tuple<string, string>> _reals = new List<Tuple<string, string>>(); // (显示, 真名；真名 null = 分组头)

        public NodePickerDialog(MainForm main, JObject settings)
        {
            _main = main;
            _settings = settings;
            Text = "选择线路";
            Width = 580;
            Height = 560;
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
            _title = new Label();
            _title.Text = "正在测速…";
            _title.AutoSize = true;
            _title.MaximumSize = new Size(532, 0);
            App.AddRow(t, ref row, _title, 6);

            _search = new TextBox();
            _search.Dock = DockStyle.Fill;
            App.SetCue(_search, "输入地区或节点名过滤…");
            _search.TextChanged += delegate { ApplyFilter(); };
            App.AddRow(t, ref row, _search, 6);

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.Margin = new Padding(0, 0, 0, 8);
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HeaderStyle = ColumnHeaderStyle.None;
            _list.Columns.Add("", 520);
            _list.ShowGroups = true;
            _list.DoubleClick += delegate { TryAccept(); };
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 400));
            t.Controls.Add(_list, 0, row);
            row++;

            var btns = new FlowLayoutPanel();
            btns.AutoSize = true;
            btns.WrapContents = false;
            Button okBtn = new Button();
            okBtn.Text = "确定";
            okBtn.AutoSize = true;
            okBtn.Click += delegate { TryAccept(); };
            Button cancelBtn = new Button();
            cancelBtn.Text = "取消";
            cancelBtn.AutoSize = true;
            cancelBtn.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            btns.Controls.Add(okBtn);
            btns.Controls.Add(cancelBtn);
            App.AddRow(t, ref row, btns, 0);

            LoadNodesAsync();
        }

        void TryAccept()
        {
            if (_list.SelectedItems.Count == 0) return;
            string choice = _list.SelectedItems[0].Tag as string;
            if (choice == null) return;
            string gname = _gname;
            Task.Run(delegate ()
            {
                try
                {
                    ProxyCore.CoreApi("/proxies/" + Uri.EscapeDataString(gname), "PUT",
                        App.NewKVObj("name", choice));
                    BeginInvoke(new Action(delegate
                    {
                        string locked = Regions.RegionOf(choice) ?? "";
                        App.Set(_settings, "locked_region", locked);
                        App.Set(_settings, "locked_group", gname);
                        App.Set(_settings, "sticky_node", choice);
                        App.SaveSettings(_settings);
                        _main.OnNodePicked(choice);
                        DialogResult = DialogResult.OK;
                        Close();
                        string note = App.CoreInUse ? "" : "\r\n（浏览器要等保存并重启后才走核心）";
                        string lockNote = locked.Length > 0
                            ? "\r\n已锁定地区：" + locked + "（失效时只在此地区内自动切换）"
                            : "\r\n这条线路认不出地区，自动切换不会跨区乱切";
                        MessageBox.Show(_main, "当前线路：" + choice + note + lockNote, "已切换",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        MessageBox.Show(this, "切换失败：" + ex.Message, "切换失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
            });
        }

        void ApplyFilter()
        {
            string q = _search.Text.Trim().ToLowerInvariant();
            foreach (ListViewItem it in _list.Items)
            {
                if (it.Tag == null) continue;
                it.Selected = false;
                bool hide = q.Length > 0 && it.Text.ToLowerInvariant().IndexOf(q) < 0;
                it.ForeColor = hide ? Color.LightGray : SystemColors.WindowText;
                it.BackColor = hide ? Color.WhiteSmoke : SystemColors.Window;
            }
        }

        void LoadNodesAsync()
        {
            Task.Run(delegate ()
            {
                try
                {
                    if (!ProxyCore.CoreAlive()) ProxyCore.StartCore();
                    if (!ProxyCore.CoreAlive())
                    {
                        BeginInvoke(new Action(delegate
                        {
                            MessageBox.Show(this, "核心起不来：" + App.CoreState + "\r\n订阅先点「保存订阅并更新」存好，再看 Muse数据\\代理核心\\core.log 里的报错。",
                                "核心没在运行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            DialogResult = DialogResult.Cancel;
                            Close();
                        }));
                        return;
                    }
                    Tuple<string, JObject> g = Regions.MainSelectorGroup();
                    if (g.Item1 == null)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            MessageBox.Show(this, "订阅里没有策略组。", "核心没在运行",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            DialogResult = DialogResult.Cancel;
                            Close();
                        }));
                        return;
                    }
                    _gname = g.Item1;
                    JArray allArr = g.Item2["all"] as JArray;
                    var names = new List<string>();
                    if (allArr != null)
                    {
                        foreach (JToken t in allArr) names.Add((string)t);
                    }
                    var delays = new Dictionary<string, double>();
                    bool tested = false;
                    try
                    {
                        JToken dm = ProxyCore.CoreApi("/group/" + Uri.EscapeDataString(_gname)
                            + "/delay?timeout=3500&url=https://www.gstatic.com/generate_204");
                        JObject dmo = dm as JObject;
                        if (dmo != null)
                        {
                            tested = true;
                            foreach (JProperty p in dmo.Properties())
                            {
                                if (p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float)
                                    delays[p.Name] = (double)p.Value;
                            }
                        }
                    }
                    catch { delays.Clear(); }
                    var types = new Dictionary<string, string>();
                    try
                    {
                        JToken pr = ProxyCore.CoreApi("/proxies");
                        JObject proxies = pr["proxies"] as JObject;
                        if (proxies != null)
                        {
                            foreach (JProperty p in proxies.Properties())
                            {
                                JObject info = p.Value as JObject;
                                if (info != null) types[p.Name] = (string)info["type"] ?? "";
                            }
                        }
                    }
                    catch { }
                    var groupsFirst = new List<Tuple<string, string>>();
                    var fast = new List<Tuple<string, string>>();
                    var dead = new List<Tuple<string, string>>();
                    foreach (string n in names)
                    {
                        string tp;
                        types.TryGetValue(n, out tp);
                        if (tp == "Selector" || tp == "URLTest" || tp == "Fallback" || tp == "LoadBalance")
                            groupsFirst.Add(Tuple.Create(n, n));
                        else if (delays.ContainsKey(n))
                            fast.Add(Tuple.Create(n + " — " + (int)delays[n] + "ms", n));
                        else
                            dead.Add(Tuple.Create(n + (tested ? " — 超时" : " — 未测速"), n));
                    }
                    fast.Sort(delegate (Tuple<string, string> a, Tuple<string, string> b)
                    {
                        return delays[a.Item2].CompareTo(delays[b.Item2]);
                    });
                    var byRegion = new Dictionary<string, List<Tuple<string, string>>>();
                    var regionOrder = new List<string>();
                    foreach (Tuple<string, string> item in fast.Concat(dead))
                    {
                        string r = Regions.RegionOf(item.Item2) ?? "其他地区";
                        List<Tuple<string, string>> lst;
                        if (!byRegion.TryGetValue(r, out lst))
                        {
                            lst = new List<Tuple<string, string>>();
                            byRegion[r] = lst;
                            regionOrder.Add(r);
                        }
                        lst.Add(item);
                    }
                    regionOrder.Sort(delegate (string a, string b)
                    {
                        double ba = BestDelay(byRegion[a], delays);
                        double bb = BestDelay(byRegion[b], delays);
                        int c = ba.CompareTo(bb);
                        return c != 0 ? c : string.Compare(a, b, StringComparison.Ordinal);
                    });
                    string current = (string)g.Item2["now"] ?? "";
                    BeginInvoke(new Action(delegate
                    {
                        _title.Text = "策略组：" + _gname + "（已按地区归类，地区内按速度从快到慢，超时在最后）";
                        _list.BeginUpdate();
                        _list.Groups.Clear();
                        _list.Items.Clear();
                        _reals.Clear();
                        if (groupsFirst.Count > 0)
                        {
                            var grp = new ListViewGroup("策略组（" + groupsFirst.Count + "）");
                            _list.Groups.Add(grp);
                            foreach (Tuple<string, string> p in groupsFirst)
                            {
                                var it = new ListViewItem(p.Item1, grp);
                                it.Tag = p.Item2;
                                _list.Items.Add(it);
                            }
                        }
                        foreach (string r in regionOrder)
                        {
                            var items = byRegion[r];
                            var grp = new ListViewGroup(r + "（" + items.Count + "）");
                            _list.Groups.Add(grp);
                            foreach (Tuple<string, string> p in items)
                            {
                                var it = new ListViewItem(p.Item1, grp);
                                it.Tag = p.Item2;
                                _list.Items.Add(it);
                                if (p.Item2 == current) it.Selected = true;
                            }
                        }
                        _list.EndUpdate();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        MessageBox.Show(this, "连不上内置核心：" + ex.Message, "核心没在运行",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = DialogResult.Cancel;
                        Close();
                    }));
                }
            });
        }

        static double BestDelay(List<Tuple<string, string>> items, Dictionary<string, double> delays)
        {
            double best = double.MaxValue;
            foreach (Tuple<string, string> it in items)
            {
                double d;
                if (delays.TryGetValue(it.Item2, out d) && d < best) best = d;
            }
            return best == double.MaxValue ? (1 << 30) : best;
        }
    }
}
