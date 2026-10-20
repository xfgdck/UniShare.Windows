using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace UniShare.UI.Views
{
    public enum DialogTone
    {
        Info,
        Success,
        Warning,
        Error,
        Confirm,
        DangerConfirm
    }

    public partial class MessageDialog : FluentWindow
    {
        private static UIElement? s_activeToastElement;
        private static Panel? s_activeToastParent;
        private static DispatcherTimer? s_activeToastTimer;

        public MessageDialog(
            string title,
            string message,
            DialogTone tone = DialogTone.Info,
            string confirmText = "知道了",
            string? cancelText = null)
        {
            InitializeComponent();

            Title = title;
            TxtTitle.Text = title;
            TxtMessage.Text = message;
            BtnConfirm.Content = confirmText;

            if (!string.IsNullOrEmpty(cancelText))
            {
                BtnCancel.Content = cancelText;
                BtnCancel.Visibility = Visibility.Visible;
            }
            else
            {
                BtnCancel.Visibility = Visibility.Collapsed;
            }

            ApplyToneVisuals(tone);
        }

        private void ApplyToneVisuals(DialogTone tone)
        {
            switch (tone)
            {
                case DialogTone.Success:
                    TxtIconSymbol.Text = "✓";
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // #10B981
                    IconBadge.Background = new SolidColorBrush(Color.FromArgb(30, 16, 185, 129));
                    BtnConfirm.Appearance = ControlAppearance.Primary;
                    break;

                case DialogTone.Warning:
                    TxtIconSymbol.Text = "!";
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // #F59E0B
                    IconBadge.Background = new SolidColorBrush(Color.FromArgb(30, 245, 158, 11));
                    BtnConfirm.Appearance = ControlAppearance.Primary;
                    break;

                case DialogTone.Error:
                    TxtIconSymbol.Text = "✕";
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // #EF4444
                    IconBadge.Background = new SolidColorBrush(Color.FromArgb(30, 239, 68, 68));
                    BtnConfirm.Appearance = ControlAppearance.Primary;
                    break;

                case DialogTone.Confirm:
                    TxtIconSymbol.Text = "?";
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // #3B82F6
                    IconBadge.Background = new SolidColorBrush(Color.FromArgb(30, 59, 130, 246));
                    BtnConfirm.Appearance = ControlAppearance.Primary;
                    break;

                case DialogTone.DangerConfirm:
                    TxtIconSymbol.Text = "!";
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // #EF4444
                    IconBadge.Background = new SolidColorBrush(Color.FromArgb(30, 239, 68, 68));
                    BtnConfirm.Appearance = ControlAppearance.Danger;
                    break;

                case DialogTone.Info:
                default:
                    TxtIconSymbol.Text = "i";
                    TxtIconSymbol.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // #3B82F6
                    IconBadge.Background = new SolidColorBrush(Color.FromArgb(30, 59, 130, 246));
                    BtnConfirm.Appearance = ControlAppearance.Primary;
                    break;
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
            else if (e.Key == Key.Enter)
            {
                DialogResult = true;
                Close();
            }
        }

        private static bool ShowCore(
            string title,
            string message,
            DialogTone tone,
            string confirmText,
            string? cancelText = null,
            Window? owner = null)
        {
            var dialog = new MessageDialog(title, message, tone, confirmText, cancelText);

            Window? targetOwner = owner
                ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w != dialog && w.IsActive && w.IsVisible)
                ?? Application.Current?.Windows.OfType<Window>().LastOrDefault(w => w != dialog && w.IsVisible);

            if (targetOwner != null && targetOwner.IsVisible)
            {
                dialog.Owner = targetOwner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Topmost = true;
            }

            return dialog.ShowDialog() == true;
        }

        private static void DismissActiveToast()
        {
            try
            {
                s_activeToastTimer?.Stop();
                s_activeToastTimer = null;

                if (s_activeToastParent != null && s_activeToastElement != null)
                {
                    s_activeToastParent.Children.Remove(s_activeToastElement);
                }

                s_activeToastElement = null;
                s_activeToastParent = null;
            }
            catch
            {
                // 忽略清理轻提示时的非关键 UI 异常
            }
        }

        public static void ShowToast(
            string text = "复制成功",
            int durationMs = 1100,
            Window? owner = null)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;

                if (!app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.InvokeAsync(() => ShowToast(text, durationMs, owner));
                    return;
                }

                DismissActiveToast();

                Window? targetWindow = owner
                    ?? app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                    ?? app.MainWindow;

                if (targetWindow?.Content is not Grid rootGrid)
                {
                    return;
                }

                var iconBadge = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text = "✓",
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };

                var label = new System.Windows.Controls.TextBlock
                {
                    Text = text,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                row.Children.Add(iconBadge);
                row.Children.Add(label);

                var pillBorder = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 10, 18, 10),
                    Background = new SolidColorBrush(Color.FromArgb(245, 42, 42, 46)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Effect = new DropShadowEffect
                    {
                        BlurRadius = 18,
                        ShadowDepth = 4,
                        Opacity = 0.5,
                        Color = Colors.Black
                    },
                    Child = row
                };

                int rowSpan = Math.Max(1, rootGrid.RowDefinitions.Count);
                int colSpan = Math.Max(1, rootGrid.ColumnDefinitions.Count);
                Grid.SetRow(pillBorder, 0);
                Grid.SetColumn(pillBorder, 0);
                Grid.SetRowSpan(pillBorder, rowSpan);
                Grid.SetColumnSpan(pillBorder, colSpan);
                Panel.SetZIndex(pillBorder, 9999);

                rootGrid.Children.Add(pillBorder);
                s_activeToastParent = rootGrid;
                s_activeToastElement = pillBorder;

                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(durationMs)
                };
                timer.Tick += (_, _) => DismissActiveToast();
                s_activeToastTimer = timer;
                timer.Start();
            }
            catch
            {
                // 确保轻提示异常绝对不影响主业务
            }
        }

        public static void ShowInfo(string message, string title = "提示", string confirmText = "知道了", Window? owner = null)
        {
            ShowCore(title, message, DialogTone.Info, confirmText, null, owner);
        }

        public static void ShowSuccess(string message, string title = "操作成功", string confirmText = "知道了", Window? owner = null)
        {
            ShowCore(title, message, DialogTone.Success, confirmText, null, owner);
        }

        public static void ShowWarning(string message, string title = "操作提示", string confirmText = "知道了", Window? owner = null)
        {
            ShowCore(title, message, DialogTone.Warning, confirmText, null, owner);
        }

        public static void ShowError(string message, string title = "发生错误", string confirmText = "知道了", Window? owner = null)
        {
            ShowCore(title, message, DialogTone.Error, confirmText, null, owner);
        }

        public static bool ShowConfirm(
            string message,
            string title = "确认操作",
            string confirmText = "确定",
            string cancelText = "取消",
            bool isDanger = false,
            Window? owner = null)
        {
            return ShowCore(
                title,
                message,
                isDanger ? DialogTone.DangerConfirm : DialogTone.Confirm,
                confirmText,
                cancelText,
                owner);
        }
    }
}
