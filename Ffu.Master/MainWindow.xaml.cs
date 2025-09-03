using System;
using System.IO.Ports;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Ffu.Master
{
    public partial class MainWindow : Window
    {
        private readonly object _ioSync = new object();
        private readonly Dictionary<int, int> _perTarget = new();
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private Task? _sendTask;
        private List<int> _ids = new();   // 여러 ID 저장
        private int _cursor;
        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, __) => Cleanup();
        }

        #region Control Events : Click, Checked, Log
        private void OnChkChecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && TryGetId(cb.Tag, out int id))
            {
                SendSetOnce(id, 800);
            }
        }
        private void OnChkUnchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && TryGetId(cb.Tag, out int id))
            {
                SendSetOnce(id, 0);
            }
        }
        private void OnUpClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && TryGetId(fe.Tag, out int id))
            {
                var rpm = Math.Clamp(GetCurrentRpm(id) + 100, 0, 1500);
                if (FindName($"TxtRpm{id}") is TextBox tb) tb.Text = rpm.ToString();
                SendSetOnce(id, rpm);
            }
        }
        private void OnDownClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && TryGetId(fe.Tag, out int id))
            {
                var rpm = Math.Clamp(GetCurrentRpm(id) - 100, 0, 1500);
                if (FindName($"TxtRpm{id}") is TextBox tb) tb.Text = rpm.ToString();
                SendSetOnce(id, rpm);
            }
        }
        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var com = TxtCom.Text.Trim(); // e.g. COM3

                _port = new SerialPort(com, 9600, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 200,
                    WriteTimeout = 200
                };
                _port.Open();

                ScanSlaveID();

                Log($"OPEN {_port.PortName} 9600");
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
            _ids = ParseIdSet(TxtIds.Text);
            if (_ids.Count == 0) { Log("IDs empty or invalid. ex) 1,3,5-7"); return; }

            _cts = new CancellationTokenSource();
            _sendTask = Task.Run(() => SendLoop(_cts.Token));

            BtnStart.IsEnabled = false; BtnStop.IsEnabled = true;
        }
        private async void BtnRescan_Click(object sender, RoutedEventArgs e)
        {
            if (_port == null || !_port.IsOpen) { Log("[DISC] port not open"); return; }

            // 1) 루프 돌고 있으면 잠깐 멈춤
            bool wasRunning = _cts != null && _sendTask != null && !_sendTask.IsCompleted;
            if (wasRunning) { _cts.Cancel(); try { await _sendTask; } catch { } }

            BtnStart.IsEnabled = false; BtnStop.IsEnabled = false;

            try
            {
                // 2) 백그라운드 재스캔 (동일 로직 재사용)
                var ids = await Task.Run(() => SlaveID_ScanSync(start: 1, end: 64, retries: 1, interDelayMs: 8, readTimeoutOverride: 100));
                // 3) 결과 반영 + 캐시 저장
                TxtIds.Text = string.Join(",", ids);
                SaveIdsCache(_port!.PortName, ids);
                lock (_ioSync) { _ids = new List<int>(ids); } // <== 추가
                Log($"[DISC] rescan: {TxtIds.Text}");
            }
            catch (Exception ex)
            {
                Log($"[DISC] rescan err: {ex.Message}");
            }
            finally
            {
                // 4) 필요하면 루프 재시작
                if (wasRunning)
                {
                    _cts = new CancellationTokenSource();
                    _sendTask = Task.Run(() => SendLoop(_cts.Token));
                    BtnStart.IsEnabled = false; BtnStop.IsEnabled = true;
                }
                else
                {
                    BtnStart.IsEnabled = true; BtnStop.IsEnabled = false;
                }
            }
        }
        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnStart.IsEnabled = true; BtnStop.IsEnabled = false;
        }
        private void Log(string s)
        {
            Dispatcher.Invoke(() => { TxtLog.AppendText(s + Environment.NewLine); TxtLog.ScrollToEnd(); });
        }
        #endregion

        #region Send / Loop
        private void SendSetOnce(int id, int rpm)
        {
            if (_port == null || !_port.IsOpen) { Log("Port not open"); return; }
            rpm = Math.Clamp(rpm, 0, 1500);
            var req = new byte[7];
            req[0] = 0x49; req[1] = 0x53; req[2] = (byte)id; req[3] = 0x06;
            var (lo, hi) = EncodeRpmLE(rpm);
            req[4] = lo; req[5] = hi; req[6] = SumChecksum(req, 6);
            try
            {
                lock (_ioSync) // 루프와 충돌 방지
                {
                    _port.Write(req, 0, req.Length);
                    Log($"> {Hex(req)}");
                    // (옵션) 간단 응답 로깅
                    try
                    {
                        var buf = new byte[32];
                        int got = _port.Read(buf, 0, buf.Length);
                        if (got >= 9 && buf[0] == 0x44 && buf[1] == 0x54)
                        {
                            if (buf[8] == SumChecksum(buf, 8))
                                Log($"< DT id={buf[2]} cmd=0x{buf[3]:X2} rpm={DecodeRpmLE(buf[6], buf[7])}");
                        }
                    }
                    catch (TimeoutException) { /* ignore */ }
                }
            }
            catch (Exception ex) { Log($"SetOnce ERR: {ex.Message}"); }
        }

        private async Task SendLoop(CancellationToken ct)
        {
            // 공통 프레임 버퍼 (7바이트 고정)
            var req = new byte[7];
            req[0] = 0x49; // 'I'
            req[1] = 0x53; // 'S'

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 1) write 계속 보내기 (콤보박스 선택)
                    //(bool isSet, int targetRpm) = Dispatcher.Invoke(() =>
                    //{
                    //    bool _isSet = (CmbMode == 1);
                    //    int rpm = 0; 
                    //    if (rpm < 0) rpm = 0; if (rpm > 1500) rpm = 1500;
                    //    return (_isSet, rpm);
                    //});
                    bool isSet = false;
                    int targetRpm = 0;
                    int id;
                    lock (_ioSync)
                    {
                        if (_ids.Count == 0) continue; // 혹시 비었으면 skip
                        id = _ids[_cursor++ % _ids.Count];
                    }
                    req[2] = (byte)id;

                    if (isSet)
                    {
                        // CMD 0x06 + 타겟 RPM(LE)
                        req[3] = 0x06;
                        var (lo, hi) = EncodeRpmLE(targetRpm);
                        req[4] = lo;   // LSB
                        req[5] = hi;   // MSB
                    }
                    else
                    {
                        // CMD 0x05 (Request)
                        req[3] = 0x05;
                        req[4] = 0x00;
                        req[5] = 0x00;
                    }

                    req[6] = SumChecksum(req, 6); // CS 제외 합의 LSB

                    _port!.Write(req, 0, req.Length);
                    Log($"> {Hex(req)}");

                    // (옵션) 응답 파싱 — 단순형(부분 프레임은 그냥 로그)
                    try
                    {
                        var buf = new byte[32];
                        int got = _port.Read(buf, 0, buf.Length); // ReadTimeout=200ms
                        if (got >= 9 && buf[0] == 0x44 && buf[1] == 0x54) // 'D','T'
                        {
                            byte rid = buf[2];
                            byte rcmd = buf[3];
                            byte lo = buf[6], hi = buf[7];
                            byte cs = buf[8];
                            byte calc = SumChecksum(buf, 8);
                            if (cs == calc)
                            {
                                int rrpm = DecodeRpmLE(lo, hi);
                                Log($"< DT ID={rid} CMD=0x{rcmd:X2} RPM={rrpm} [{Hex(buf.AsSpan(0, got))}]");
                            }
                            else
                            {
                                Log($"< CS ERR [{Hex(buf.AsSpan(0, got))}]");
                            }
                        }
                        else if (got > 0)
                        {
                            Log($"< {Hex(buf.AsSpan(0, got))}");
                        }
                    }
                    catch (TimeoutException) { /* ignore */ }

                    await Task.Delay(120, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* 종료 */ }
                catch (Exception ex)
                {
                    Log($"Send ERR: {ex.Message}");
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }
        }

        private int GetCurrentRpm(int id)
        {
            if (_perTarget.TryGetValue(id, out var v)) return v;
            // TextBox를 이름으로 찾아서 파싱 (초기 0)
            var tb = (TextBox?)FindName($"TxtRpm{id}");
            if (tb != null && int.TryParse(tb.Text, out var t)) return t;
            return 0;
        }

        private static bool TryGetId(object? tag, out int id)
        {
            if (tag is int v) { id = v; return true; }
            if (int.TryParse(tag?.ToString(), out v)) { id = v; return true; }
            id = 0; return false;
        }

        #endregion

        #region Utils : Parsing, Checksum, Hex
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
        // RPM 인코딩/디코딩 (리틀엔디언)
        static (byte lo, byte hi) EncodeRpmLE(int rpm)
        {
            if (rpm < 0) rpm = 0;
            if (rpm > 1500) rpm = 1500;
            return ((byte)(rpm & 0xFF), (byte)((rpm >> 8) & 0xFF));
        }
        static int DecodeRpmLE(byte lo, byte hi) => (hi << 8) | lo;


        static byte SumChecksum(ReadOnlySpan<byte> frame)
        {
            int sum = 0;
            for (int i = 0; i < frame.Length; i++) sum += frame[i];
            return (byte)(sum & 0xFF);
        }

        // ② 배열 + 길이  (req, 6) 같은 호출용
        static byte SumChecksum(byte[] frame, int len)
            => SumChecksum(frame.AsSpan(0, len));

        // ③ List<byte> 범위용 
        static byte SumChecksum(List<byte> src, int offset, int length)
        {
            int sum = 0;
            for (int i = 0; i < length; i++) sum += src[offset + i];
            return (byte)(sum & 0xFF);
        }


        static string Hex(ReadOnlySpan<byte> span)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < span.Length; i++) sb.Append(span[i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }
        #endregion


        private void Cleanup()
        {
            try { _cts?.Cancel(); _sendTask?.Wait(200); } catch { }
            try { _port?.Close(); _port?.Dispose(); } catch { }
            _cts = null; _sendTask = null; _port = null;
            BtnOpen.IsEnabled = true; BtnClose.IsEnabled = false; BtnStart.IsEnabled = false; BtnStop.IsEnabled = false;
            Log("CLOSED");
        }
    }
}
