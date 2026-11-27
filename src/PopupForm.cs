using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Muse
{
    // 新窗口弹窗（对应 py View.createWindow）：与主账号共用同一环境，
    // 跳到 http(s) 就交给主窗口加载后关掉；9 秒还打不开就甩给本地浏览器。
    public class PopupForm : Form
    {
        public WebView2 View { get; private set; }
        readonly MainForm _main;
        bool _landed = false;
        bool _closed = false;

        public PopupForm(MainForm main, CoreWebView2Environment env, string startUri)
        {
            _main = main;
            Text = "Muse";
            Size = new Size(900, 650);
            StartPosition = FormStartPosition.CenterParent;
            View = new WebView2();
            View.Dock = DockStyle.Fill;
            Controls.Add(View);
            Shown += delegate { InitAsync(env, startUri); };
        }

        async void InitAsync(CoreWebView2Environment env, string startUri)
        {
            try
            {
                await View.EnsureCoreWebView2Async(env);
            }
            catch { Close(); return; }
            CoreWebView2 core = View.CoreWebView2;
            core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs e)
            {
                try
                {
                    Uri u = new Uri(e.Uri);
                    if ((u.Scheme == "http" || u.Scheme == "https"))
                    {
                        _main.NavigateCurrent(e.Uri);
                        var t = new Timer();
                        t.Interval = 500;
                        t.Tick += delegate { t.Stop(); SafeClose(); };
                        t.Start();
                    }
                }
                catch { }
            };
            core.NavigationCompleted += delegate (object s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _landed = e.IsSuccess;
            };
            core.DownloadStarting += delegate (object s, CoreWebView2DownloadStartingEventArgs e)
            {
                try
                {
                    string uri = e.DownloadOperation.Uri;
                    if (uri.StartsWith("http://") || uri.StartsWith("https://"))
                    {
                        e.Cancel = true;
                        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
                        SafeClose();
                    }
                }
                catch { }
            };
            var early = new Timer();
            early.Interval = 4000;
            early.Tick += delegate
            {
                early.Stop();
                try
                {
                    string sc = View.Source == null ? "" : View.Source.Scheme;
                    if (sc == "" || sc == "about") SafeClose();
                }
                catch { }
            };
            early.Start();
            var fb = new Timer();
            fb.Interval = 9000;
            fb.Tick += delegate
            {
                fb.Stop();
                try
                {
                    Uri u = View.Source;
                    if (!_landed && u != null && (u.Scheme == "http" || u.Scheme == "https"))
                    {
                        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
                        SafeClose();
                    }
                }
                catch { }
            };
            fb.Start();
            if (!string.IsNullOrEmpty(startUri))
            {
                try { core.Navigate(startUri); } catch { }
            }
        }

        void SafeClose()
        {
            if (_closed) return;
            _closed = true;
            try { Close(); } catch { }
        }
    }
}
