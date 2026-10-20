using System;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop.Views
{
    public partial class OnlineStatusWindow
    {
        private readonly OnlineStatusViewModel _viewModel;

        public OnlineStatusWindow(OnlineStatusViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = _viewModel;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _viewModel.Dispose();
        }
    }
}
