using System;
using System.IO.Ports;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using System.IO;

namespace Ffu.Master
{
    public partial class MainWindow : Window
    {
        private readonly OverView _overView;
        private readonly SettingView _settingView;
        public MainWindow()
        {
            InitializeComponent();
            
            _overView = new OverView();
            _settingView = new SettingView(_overView);

            MainFrame.Navigate(_overView);

            Closed += (_, __) => Cleanup();
        }
        private void BtnSetting_Click(object sender, RoutedEventArgs e)
            => MainFrame.Navigate(_settingView);

        private void BtnExit_Click(object sender, RoutedEventArgs e)
            => MainFrame.Navigate(_overView);
        private void Cleanup()
        {
            try
            {
                dynamic page = MainFrame.Content;
                page.Cleanup();
            }
            catch { }
        }
    }
}
