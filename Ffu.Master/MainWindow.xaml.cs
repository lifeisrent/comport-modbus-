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
        private TabMainView _tabMainView;

        public MainWindow()
        {
            InitializeComponent();
            _tabMainView = new TabMainView();
            MainFrame.Navigate(_tabMainView);
            this.Closed += (_, __) => _tabMainView.Cleanup();
        }
    }
}
