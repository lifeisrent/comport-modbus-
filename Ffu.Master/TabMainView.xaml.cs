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
using static System.Windows.Forms.VisualStyles.VisualStyleElement.ToolTip;

namespace Ffu.Master
{
    /// <summary>
    /// TabMainView.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class TabMainView : Page
    {
        private readonly OverView _overView;
        private readonly SettingView _settingView;
        public TabMainView()
        {
            InitializeComponent();
            _overView = new OverView();
            _settingView = new SettingView(_overView);

            MainFrame.Navigate(_overView);

        }
        private void BtnSetting_Click(object sender, RoutedEventArgs e)
    => MainFrame.Navigate(_settingView);

        private void BtnExit_Click(object sender, RoutedEventArgs e)
            => MainFrame.Navigate(_overView);
        public void Cleanup()
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
