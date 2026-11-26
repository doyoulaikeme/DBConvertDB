using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DbConvert.Gui
{
    public enum DialogKind
    {
        Info,
        Success,
        Warning,
        Error,
        Question
    }

    /// <summary>
    /// 与应用主界面同一视觉语言的消息弹框：圆角卡片、状态色标题区、柔和阴影。
    /// 用来替代系统 MessageBox，避免 Win7/10 原生灰底弹框与自绘主窗口割裂。
    /// </summary>
    public partial class AppDialog : Window
    {
        public bool Result { get; private set; }

        private AppDialog()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
                BtnOk.Focus();
            };
        }

        public static void Show(Window owner, string message, string title = "提示",
            DialogKind kind = DialogKind.Info, string okText = "知道了")
        {
            var dlg = new AppDialog();
            if (owner != null && owner.IsVisible)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            dlg.Apply(title, message, kind, okText, null, false);
            dlg.ShowDialog();
        }

        public static bool Confirm(Window owner, string message, string title = "确认",
            string okText = "继续", string cancelText = "取消",
            DialogKind kind = DialogKind.Question)
        {
            var dlg = new AppDialog();
            if (owner != null && owner.IsVisible)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            dlg.Apply(title, message, kind, okText, cancelText, true);
            dlg.ShowDialog();
            return dlg.Result;
        }

        private void Apply(string title, string message, DialogKind kind,
            string okText, string cancelText, bool showCancel)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "提示" : title;
            TxtTitle.Text = Title;
            TxtMessage.Text = message ?? string.Empty;
            BtnOk.Content = string.IsNullOrWhiteSpace(okText) ? "知道了" : okText;
            BtnCancel.Content = string.IsNullOrWhiteSpace(cancelText) ? "取消" : cancelText;
            BtnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

            string accent;
            string header;
            string glyph;
            switch (kind)
            {
                case DialogKind.Success:
                    accent = "#16A34A";
                    header = "#ECFDF5";
                    glyph = "\uE73E";
                    break;
                case DialogKind.Warning:
                    accent = "#D97706";
                    header = "#FFF7ED";
                    glyph = "\uE7BA";
                    break;
                case DialogKind.Error:
                    accent = "#DC2626";
                    header = "#FEF2F2";
                    glyph = "\uE783";
                    break;
                case DialogKind.Question:
                    accent = "#2563EB";
                    header = "#EFF6FF";
                    glyph = "\uE897";
                    break;
                default:
                    accent = "#2563EB";
                    header = "#EFF6FF";
                    glyph = "\uE946";
                    break;
            }

            IconCircle.Background = FromHex(accent);
            HeaderBg.Background = FromHex(header);
            TxtIcon.Text = glyph;

            // 只有确认框把焦点给“继续”，单按钮框仍给“知道了”
            if (showCancel) BtnOk.IsDefault = true;
        }

        private static Brush FromHex(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Result = true;
            DialogResult = true;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }
    }
}
