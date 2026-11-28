using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Muse
{
    // 新窗口弹窗：与主账号共用同一环境（登录态带过去，不用重新登录），自带标题栏关闭 X；
    // 只显示页面，不推给主窗口、不甩给系统浏览器。主窗口的 NewWindowRequested 用
    // GetDeferral 等视图初始化完再把视图交过去，v18 就是没等初始化完才漏到系统浏览器。
    public class PopupForm : Form
    {
        public WebView2 View { get; private set; }
        readonly MainForm _main;
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
            // 标题跟页面走，比如“Account settings | Meta”，一看就知道是哪个窗口
            core.DocumentTitleChanged += delegate (object s, object e)
            {
                try { if (!string.IsNullOrEmpty(core.DocumentTitle)) Text = core.DocumentTitle; } catch { }
            };
            // 页面自己要关窗口（比如点网站里的关闭）就关掉本窗口
            core.WindowCloseRequested += delegate (object s, object e) { SafeClose(); };
            // 弹窗里再开新窗口：同样开一个独立小窗口，不推给主窗口、不甩给系统浏览器
            core.NewWindowRequested += async delegate (object s2, CoreWebView2NewWindowRequestedEventArgs e2)
            {
                CoreWebView2Deferral def2 = null;
                try
                {
                    Uri u2 = string.IsNullOrEmpty(e2.Uri) ? null : new Uri(e2.Uri);
                    if (u2 == null || (u2.Scheme != "http" && u2.Scheme != "https")) { e2.Handled = true; return; }
                    def2 = e2.GetDeferral();
                    var popup2 = new PopupForm(_main, env, null);
                    await popup2.View.EnsureCoreWebView2Async(env);
                    e2.NewWindow = popup2.View.CoreWebView2;
                    e2.Handled = true;
                    popup2.Show();
                }
                catch { e2.Handled = false; }
                finally { if (def2 != null) def2.Complete(); }
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
            // startUri 只在独立创建弹窗时用；NewWindowRequested 走 deferral 时传 null，
            // 页面地址由浏览器自己填进已初始化好的视图，不在这里再导一次
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
