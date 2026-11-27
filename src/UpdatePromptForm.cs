using System;
using System.Drawing;
using System.Windows.Forms;

namespace Muse
{
    // 发现新版本的提示框：10 秒无操作自动取消，不强制更新
    class UpdatePromptForm : Form
    {
        int _left = 10;
        Timer _timer;
        Label _countLabel;

        public UpdatePromptForm(string ver, string notes)
        {
            Text = "发现新版本";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 190);

            Label msg = new Label();
            msg.Text = "发现新版本 " + ver + "（当前 " + App.Version + "）。\r\n" + notes
                + "\r\n\r\n现在下载并更新吗？更新时会重新编译，约一两分钟，编好后自动重启。";
            msg.AutoSize = false;
            msg.Location = new Point(16, 14);
            msg.Size = new Size(408, 100);
            Controls.Add(msg);

            _countLabel = new Label();
            _countLabel.Location = new Point(16, 120);
            _countLabel.Size = new Size(200, 20);
            _countLabel.Text = "10 秒后自动取消";
            Controls.Add(_countLabel);

            Button ok = new Button();
            ok.Text = "立即更新";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(240, 150);
            ok.Size = new Size(90, 28);
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(340, 150);
            cancel.Size = new Size(84, 28);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate
            {
                _left--;
                if (_left <= 0)
                {
                    _timer.Stop();
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
                else
                {
                    _countLabel.Text = _left + " 秒后自动取消";
                }
            };
            _timer.Start();
        }
    }
}
