using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Ffu.Master
{
    /// <summary>
    /// SettingView.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class SettingView : Page
    {
        private readonly OverView _overView;
        public string IdsText
        {
            get => TxtIds.Text;
            set => TxtIds.Text = value;
        }
        public SettingView(OverView overView)
        {
            InitializeComponent();
            _overView = overView;
        }
        public string ComText
        {
            get => TxtCom.Text;
            set => TxtCom.Text = value;
        }

        private async void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            _overView.SetIdsText(IdsText);
            await _overView.OpenPort(ComText); // 포트 오픈 실제 수행
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _overView.Cleanup();
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            _overView.SetIdsText(IdsText);
            _overView.StartPolling();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _overView.StopPolling();
        }

        private async void BtnRescan_Click(object sender, RoutedEventArgs e)
        {
            await _overView.BtnRescan_Click(); // 포트 오픈 실제 수행
        }
    }
}
