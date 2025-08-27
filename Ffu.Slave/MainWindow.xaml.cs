using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Ffu.Slave
{
    public partial class MainWindow : Window
    {
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private int _rpmCache; // 0~1500

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
                var com = TxtCom.Text.Trim();

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
        private void TxtRpm_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!int.TryParse(TxtRpm.Text, out var rpm)) rpm = 0;
            if (rpm < 0) rpm = 0;
            if (rpm > 1500) rpm = 1500;
            Volatile.Write(ref _rpmCache, rpm); // 캐시에 저장
        }
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Cleanup();

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_port == null || !_port.IsOpen) { Log("Port not open"); return; }
            if (!int.TryParse(TxtId.Text, out var id) || id < 1 || id > 64) { Log("ID 1~64"); return; }

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => ListenLoop(id, _cts.Token));
            BtnStart.IsEnabled = false; BtnStop.IsEnabled = true;
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnStart.IsEnabled = true; BtnStop.IsEnabled = false;
        }

        private async Task ListenLoop(int myId, CancellationToken ct)
        {
            var rx = new List<byte>(64);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int b = _port!.ReadByte(); // ReadTimeout=200ms
                    if (b < 0) continue;
                    rx.Add((byte)b);

                    // 헤더 동기화 (49 53 ...)
                    while (rx.Count >= 1 && rx[0] != 0x49) rx.RemoveAt(0);
                    if (rx.Count >= 2 && rx[1] != 0x53) { rx.RemoveAt(0); continue; }

                    // 요청 프레임: 49 53 ID 05 00 00 CS (총 7바이트)
                    if (rx.Count >= 7)
                    {
                        // 인덱싱으로 직접 접근 (Span/AsSpan 제거)
                        byte id = rx[2];
                        byte cmd = rx[3];
                        byte cs = rx[6];
                        byte calc = SumChecksum(rx, 0, 6); // 아래 오버로드 추가

                        if (cs == calc && cmd == 0x05 && id == (byte)myId)
                        {
                            Log($"REQ {Hex(rx, 0, 7)}");

                            // 현재 요구: rpm==0만 응답
                            int rpm = Volatile.Read(ref _rpmCache);
                            if (rpm != 0) rpm = 0;

                            // 응답: 44 54 ID 05 00 00 [rpmH] [rpmL] CS (9바이트)
                            var resp = new byte[9];
                            resp[0] = 0x44; resp[1] = 0x54; resp[2] = (byte)myId;
                            resp[3] = 0x05; resp[4] = 0x00; resp[5] = 0x00;
                            resp[6] = (byte)((rpm >> 8) & 0xFF);
                            resp[7] = (byte)(rpm & 0xFF);
                            resp[8] = SumChecksum(resp.AsSpan(0, 8));

                            _port.Write(resp, 0, resp.Length);
                            Log($"RES {Hex(resp)}");
                        }

                        // 프레임 소비
                        rx.RemoveRange(0, 7);
                    }
                }
                catch (TimeoutException) { /* idle */ }
                catch (Exception ex)
                {
                    Log($"Loop ERR: {ex.Message}");
                    await Task.Delay(50, ct);
                }
            }
        }
        // List<byte> 범위 체크섬
        static byte SumChecksum(List<byte> src, int offset, int length)
        {
            int sum = 0;
            for (int i = 0; i < length; i++) sum += src[offset + i];
            return (byte)(sum & 0xFF);
        }

        // List<byte> 범위 HEX 출력
        static string Hex(List<byte> src, int offset, int length)
        {
            var sb = new StringBuilder(length * 3);
            for (int i = 0; i < length; i++) sb.Append(src[offset + i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        private int ParseRpmSafe()
        {
            if (!int.TryParse(TxtRpm.Text, out var rpm)) rpm = 0;
            if (rpm < 0) rpm = 0;
            if (rpm > 1500) rpm = 1500;
            return rpm;
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
            try { _cts?.Cancel(); _loopTask?.Wait(200); } catch { }
            try { _port?.Close(); _port?.Dispose(); } catch { }
            _cts = null; _loopTask = null; _port = null;
            BtnOpen.IsEnabled = true; BtnClose.IsEnabled = false; BtnStart.IsEnabled = false; BtnStop.IsEnabled = false;
            Log("CLOSED");
        }

        private void Log(string s)
        {
            Dispatcher.Invoke(() => { TxtLog.AppendText(s + Environment.NewLine); TxtLog.ScrollToEnd(); });
        }
    }
}
