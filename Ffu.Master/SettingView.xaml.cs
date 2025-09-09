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
        private const int hardcoded_MAXRPM = 2000;
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

        #if !DEBUG
                    BtnStart.Visibility = Visibility.Collapsed;
                    BtnStop.Visibility = Visibility.Collapsed;
                    // TextBlock도 숨기려면
                    DevStart.Visibility = Visibility.Collapsed;
        #endif
        }
        public string ComText
        {
            get => TxtCom.Text;
            set => TxtCom.Text = value;
        }

        private void TxtMaxRPM_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _); // 숫자 아니면 입력 무시
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

        private async void RbOpen_Checked(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtMaxRPM.Text, out int val))
            {
                MessageBox.Show("maxrpm edit error.");
                return;
            }

            if (val > hardcoded_MAXRPM)
                val = hardcoded_MAXRPM;

            FFUModel.MaxRpm = val;
            _overView.SetIdsText(IdsText);

            bool success = await _overView.OpenPort(ComText);

#if !DEBUG
                if (success)
                {
                    _overView.StartPolling(); // 릴리즈 모드에서는 Open 성공 시 바로 Start
                }
                else
                {
                }
#endif
            RbClose.IsEnabled = success;
        }

        private void RbClose_Checked(object sender, RoutedEventArgs e)
        {
            #if !DEBUG
                // Release 모드: StopPolling 먼저 실행
                BtnStop_Click(sender, e);
            #endif
            _overView.Cleanup();
        }
    }
}
