using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace UniShare.UI.Views.Pages
{
    public partial class SharesPage : UserControl
    {
        public SharesPage()
        {
            InitializeComponent();
        }

        /// <summary>
        /// “更多操作”按钮左键单击时在按钮下方弹出其上下文菜单
        /// </summary>
        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement button && button.ContextMenu is ContextMenu menu)
            {
                menu.DataContext = button.DataContext;
                menu.PlacementTarget = button;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }
    }
}
