using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Ffu.Master
{
    public partial class MainWindow : Window
    {
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private Task? _sendTask;

        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, __) => Cleanup();
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(TxtId.Text, out var id) || id < 1 || id > 64)
                    throw new ArgumentException("Slave ID: 1~64");
                var com = TxtCom.Text.Trim(); // e.g. COM3

                _port = new SerialPort(com, 9600, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 200,
                    WriteTimeout = 200
                };
                _port.Open();

                Log($"OPEN {_port.PortName} 9600-8N1");
                BtnOpen.IsEnabled = false; BtnClose.IsEnabled = true; BtnStart.IsEnabled = true;
            }
            catch (Exception ex) { Log($"Open FAIL: {ex.Message}"); }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_port == null || !_port.IsOpen) { Log("Port not open"); return; }
            if (!int.TryParse(TxtId.Text, out var id) || id < 1 || id > 64) { Log("ID 1~64"); return; }

            _cts = new CancellationTokenSource();
            _sendTask = Task.Run(() => SendLoop(id, _cts.Token));
            BtnStart.IsEnabled = false; BtnStop.IsEnabled = true;
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnStart.IsEnabled = true; BtnStop.IsEnabled = false;
        }

        private async Task SendLoop(int id, CancellationToken ct)
        {
            // Frame: 49 53 ID 05 00 00 CS
            var req = new byte[7];
            req[0] = 0x49; // 'I'
            req[1] = 0x53; // 'S'
            req[2] = (byte)id;
            req[3] = 0x05;
            req[4] = 0x00;
            req[5] = 0x00;
            req[6] = SumChecksum(req.AsSpan(0, 6));

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _port!.Write(req, 0, req.Length);
                    Log($"> {Hex(req)}");
                }
                catch (Exception ex) { Log($"Send ERR: {ex.Message}"); }

                await Task.Delay(500, ct).ConfigureAwait(false);
                // (선택) 응답 수신 로깅
                try
                {
                    var buf = new byte[256];
                    int got = _port!.Read(buf, 0, buf.Length); // 비동기처럼 사용(타임아웃 200ms)
                    if (got > 0) Log($"< {Hex(buf.AsSpan(0, got))}");
                }
                catch (TimeoutException) { /* ignore */ }
                catch (Exception ex) { Log($"Read ERR: {ex.Message}"); }
            }
        }

        static byte SumChecksum(ReadOnlySpan<byte> frame)
        {
            int sum = 0; for (int i = 0; i < frame.Length; i++) sum += frame[i];
            return (byte)(sum & 0xFF);
        }

        static string Hex(ReadOnlySpan<byte> span)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < span.Length; i++) sb.Append(span[i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        private void Cleanup()
        {
            try { _cts?.Cancel(); _sendTask?.Wait(200); } catch { }
            try { _port?.Close(); _port?.Dispose(); } catch { }
            _cts = null; _sendTask = null; _port = null;
            BtnOpen.IsEnabled = true; BtnClose.IsEnabled = false; BtnStart.IsEnabled = false; BtnStop.IsEnabled = false;
            Log("CLOSED");
        }

        private void Log(string s)
        {
            Dispatcher.Invoke(() => { TxtLog.AppendText(s + Environment.NewLine); TxtLog.ScrollToEnd(); });
        }
    }
}
