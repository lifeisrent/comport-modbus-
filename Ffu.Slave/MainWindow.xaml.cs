using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        public ObservableCollection<WatchSlot> WatchSlots { get; } = new();

        // ID→RPM 캐시(수신값 저장)
        private readonly ConcurrentDictionary<int, int> _rpmById = new();

        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, __) => Cleanup();

            for (int id = 1; id <= 6; id++)
            {
                var s = new WatchSlot { WatchId = id };
                s.PropertyChanged += WatchSlot_PropertyChanged;
                WatchSlots.Add(s);
            }

            if (DataContext == null) DataContext = this;
        }
        private void WatchSlot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(WatchSlot.WatchId)) return;
            if (sender is WatchSlot slot)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (slot.WatchId is int id && id >= 1 && id <= 64 && _rpmById.TryGetValue(id, out var rpm))
                        slot.CurrentRpm = rpm.ToString();
                    else
                        slot.CurrentRpm = "-";
                });
            }
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
        // BE 인코딩/디코딩으로 변경
        static (byte hi, byte lo) EncodeRpmBE(int rpm)
        {
            if (rpm < 0) rpm = 0;
            if (rpm > 1500) rpm = 1500;
            return ((byte)((rpm >> 8) & 0xFF), (byte)(rpm & 0xFF));
        }
        static int DecodeRpmBE(byte hi, byte lo) => (hi << 8) | lo;

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnStart.IsEnabled = true; BtnStop.IsEnabled = false;
        }

        private bool isCommLogging = true;
        private CommLogger logger = new CommLogger("rs485_slave_log.csv");

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

                        if (cs == calc && cmd == 0x06 && _idSet.Contains(id))
                        {
                            if (isCommLogging) logger.StartRequest();

                            byte hi = rx[4], lo = rx[5];
                            int rpm = DecodeRpmBE(hi, lo);
                            if (rpm < 0) rpm = 0; if (rpm > 1500) rpm = 1500;

                            _rpmById[id] = rpm;
                            UpdateWatchSlotsFor(id, rpm);
                            Log($"SET ID={id} RPM={rpm}");

                            // (변경점) ACK(0x06) 보내지 않음

                            // (신규) Target RPM 반환: 0x01, 7바이트
                            var resp = new byte[7];
                            resp[0] = 0x44; resp[1] = 0x54; resp[2] = id;
                            resp[3] = 0x01;
                            resp[4] = hi; resp[5] = lo;
                            resp[6] = SumChecksum(resp, 6);

                            int delayMs = Random.Shared.Next(2, 31);
                            await Task.Delay(delayMs, ct);
                            _port!.Write(resp, 0, resp.Length);
                            if (isCommLogging) logger.LogResponse(id, 7, resp.Length, error: false);

                            Log($"RES(TargetRPM) {Hex(resp)}");

                            rx.RemoveRange(0, 7);
                            continue;
                        }

                        if (cs == calc && cmd == 0x05 && _idSet.Contains(id))
                        {
                            if (isCommLogging) logger.StartRequest();
                            Log($"REQ {Hex(rx, 0, 7)}");

                            int rpm = _rpmById.TryGetValue(id, out var r) ? r : 0;
                            _rpmById[id] = rpm;
                            UpdateWatchSlotsFor(id, rpm);
                            var (hi, lo) = EncodeRpmBE(rpm);

                            var resp = new byte[9];
                            resp[0] = 0x44; resp[1] = 0x54; resp[2] = id;
                            resp[3] = 0x05; resp[4] = hi; resp[5] = lo;
                            resp[6] = 0x00;
                            resp[7] = 0x00;
                            resp[8] = SumChecksum(resp, 8);

                            int delayMs = Random.Shared.Next(2, 31);
                            await Task.Delay(delayMs, ct);
                            _port.Write(resp, 0, resp.Length);
                            if (isCommLogging) logger.LogResponse(id, 7, 9, error: false); // 응답 기록
                            Log($"RES {Hex(resp)}");
                        }

                        rx.RemoveRange(0, 7);
                    }
                }
                catch (TimeoutException)
                {
                    if (isCommLogging) logger.LogTimeout(0, 0); // Timeout 기록 (ID/길이 알 수 없으면 0)
                }
                catch (Exception ex)
                {
                    Log($"Loop ERR: {ex.Message}");
                    await Task.Delay(50, ct);
                }
            }
        }
        private void UpdateWatchSlotsFor(int id, int rpm)
        {
            // 해당 ID를 보고 있는 슬롯만 안전하게 갱신
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var slot in WatchSlots)
                {
                    if (slot.WatchId == id)
                        slot.CurrentRpm = rpm.ToString();
                }
            });
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

        private static int ParseWatch(string s)
    => (int.TryParse(s, out var v) && v >= 1 && v <= 64) ? v : -1;



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
