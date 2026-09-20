using System.Windows.Controls;
using ServerMonitor.ViewModels;

namespace ServerMonitor.Views
{
    public partial class HistoryView : UserControl
    {
        public HistoryView()
        {
            InitializeComponent();
        }

        /// <summary>切到本页时调用，重新拉取可用日期，避免看到过期的数据。</summary>
        public void ReloadData()
        {
            var viewModel = DataContext as HistoryViewModel;
            if (viewModel == null) return;

            viewModel.Reload();
        }
    }
}
