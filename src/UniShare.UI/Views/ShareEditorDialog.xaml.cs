using System.Windows;
using UniShare.UI.ViewModels;
using Wpf.Ui.Controls;

namespace UniShare.UI.Views
{
    public partial class ShareEditorDialog : FluentWindow
    {
        private readonly ShareEditorViewModel m_viewModel;

        public ShareEditorDialog(ShareEditorViewModel viewModel)
        {
            InitializeComponent();
            m_viewModel = viewModel;
            DataContext = viewModel;

            viewModel.CloseRequested += OnCloseRequested;
            Closed += (s, e) => viewModel.CloseRequested -= OnCloseRequested;
        }

        private void OnCloseRequested(bool saved)
        {
            DialogResult = saved;
            Close();
        }

        /// <summary>
        /// 密码框不支持绑定，由视图把密码交给视图模型；添加成功后清空
        /// </summary>
        private void BtnAddUser_Click(object sender, RoutedEventArgs e)
        {
            if (m_viewModel.TryAddUser(TxtNewPassword.Password))
            {
                TxtNewPassword.Password = string.Empty;
            }
        }
    }
}
