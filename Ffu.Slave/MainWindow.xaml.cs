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
        private HashSet<int> _idSet = new();
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
            
            var ids = ParseIdSet(TxtIds.Text);
            if (ids.Count == 0) { Log("IDs empty or invalid. ex) 1,3,5-7"); return; }

            _idSet = new HashSet<int>(ids);
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => ListenLoop(_cts.Token));
            BtnStart.IsEnabled = false; BtnStop.IsEnabled = true;
        }
        private static List<int> ParseIdSet(string text)
        {
            var set = new SortedSet<int>();
            if (string.IsNullOrWhiteSpace(text)) return new List<int>();

            foreach (var tok in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.Contains('-'))
                {
                    var p = tok.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 2 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b))
                    {
                        if (a > b) (a, b) = (b, a);
                        for (int i = a; i <= b; i++) if (i >= 1 && i <= 64) set.Add(i);
                    }
                }
                else if (int.TryParse(tok, out var v) && v >= 1 && v <= 64)
                {
                    set.Add(v);
                }
            }
            return new List<int>(set);
        }
        static (byte lo, byte hi) EncodeRpmLE(int rpm)
        {
            if (rpm < 0) rpm = 0;
            if (rpm > 1500) rpm = 1500;
            return ((byte)(rpm & 0xFF), (byte)((rpm >> 8) & 0xFF));
        }
        static int DecodeRpmLE(byte lo, byte hi) => (hi << 8) | lo;

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnStart.IsEnabled = true; BtnStop.IsEnabled = false;
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            var rx = new List<byte>(64);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int b = _port!.ReadByte(); // ReadTimeout=200ms
                    if (b < 0) continue;
                    rx.Add((byte)b);

                    // 헤더 동기화: 49 53 ...
                    while (rx.Count >= 1 && rx[0] != 0x49) rx.RemoveAt(0);
                    if (rx.Count >= 2 && rx[1] != 0x53) { rx.RemoveAt(0); continue; }

                    // 요청 프레임: 49 53 ID 05 00 00 CS (총 7바이트)
                    if (rx.Count >= 7)
                    {
                        byte id = rx[2];
                        byte cmd = rx[3];
                        byte cs = rx[6];
                        byte calc = SumChecksum(rx, 0, 6);   // CS 제외(0~5) 합의 LSB

                        // === 0x06: Target RPM Set (LE) ===
                        if (cs == calc && cmd == 0x06 && _idSet.Contains(id))
                        {
                            // DATA1=lo, DATA2=hi  (LE)
                            byte lo = rx[4], hi = rx[5];
                            int rpm = DecodeRpmLE(lo, hi);
                            if (rpm < 0) rpm = 0; if (rpm > 1500) rpm = 1500;

                            Volatile.Write(ref _rpmCache, rpm);
                            Log($"SET ID={id} RPM={rpm}");

                            // (선택) ACK 응답: 44 54 ID 06 00 00 lo hi CS
                            var ack = new byte[9];
                            ack[0] = 0x44; ack[1] = 0x54; ack[2] = id;
                            ack[3] = 0x06; ack[4] = 0x00; ack[5] = 0x00;
                            ack[6] = lo; ack[7] = hi;
                            ack[8] = SumChecksum(ack, 8);

                            _port!.Write(ack, 0, ack.Length);
                            Log($"ACK {Hex(ack)}");

                            rx.RemoveRange(0, 7);
                            continue; // 다음 프레임
                        }

                        if (cs == calc && cmd == 0x05 && _idSet.Contains(id))
                        {
                            Log($"REQ {Hex(rx, 0, 7)}");

                            int rpm = Volatile.Read(ref _rpmCache);        // 0~1500
                            var (lo, hi) = EncodeRpmLE(rpm);

                            // 응답: 44 54 ID 05 00 00 lo hi CS (LE)
                            var resp = new byte[9];
                            resp[0] = 0x44; resp[1] = 0x54; resp[2] = id;
                            resp[3] = 0x05; resp[4] = 0x00; resp[5] = 0x00;
                            resp[6] = lo;   // LE
                            resp[7] = hi;   // LE
                            resp[8] = SumChecksum(resp, 8);

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

        // byte[] + 길이 체크섬 (응답 계산용)
        static byte SumChecksum(byte[] buf, int len)
        {
            int sum = 0;
            for (int i = 0; i < len; i++) sum += buf[i];
            return (byte)(sum & 0xFF);
        }

        // HEX 도우미 (있는 버전 쓰면 생략)
        static string Hex(List<byte> src, int offset, int length)
        {
            var sb = new System.Text.StringBuilder(length * 3);
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
