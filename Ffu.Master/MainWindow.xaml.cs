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
        private const int MAXRPM = 1500; // 최대 RPM 상수 정의

        private readonly object _ioSync = new object();
        private readonly Dictionary<int, int> _perTarget = new();
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        private Task? _sendTask;
        private List<int> _ids = new();   // 여러 ID 저장
        private int _cursor;
        private CommLogger logger;
        private bool isCommLogging = true; // 추가된 필드

        public MainWindow()
        {
            InitializeComponent();
            logger = new CommLogger("rs485_log.csv");
            Closed += (_, __) => Cleanup();
        }

        #region Control Events : Click, Checked, Log
        private void OnChkChecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && TryGetId(cb.Tag, out int id))
            {
                int rpm = 800;
                if (FindName($"TxtSetRpm{id}") is TextBox tb) tb.Text = rpm.ToString();
                SendSetOnce(id, rpm);
            }
        }
        private void OnChkUnchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && TryGetId(cb.Tag, out int id))
            {
                int rpm = 0;
                if (FindName($"TxtSetRpm{id}") is TextBox tb) tb.Text = rpm.ToString();
                SendSetOnce(id, rpm);
            }
        }
        private void OnUpClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && TryGetId(fe.Tag, out int id))
                AdjustAndSend(id, +100);
        }
        private void OnDownClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && TryGetId(fe.Tag, out int id))
                AdjustAndSend(id, -100);
        }
        private async void BtnOpen_Click(object sender, RoutedEventArgs e)
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
                _port.DtrEnable = false;
                _port.RtsEnable = false;
                _port.Handshake = Handshake.None;
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();
                await Task.Delay(200);

                // ScanSlaveID(); // ← no-op 처리
                _ids = ParseIdSet(TxtIds.Text);

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
        // 단발 송신 SET(0x06): 프레임 생성 → 송신 → (미구현) 응답 파싱/로그
        private void SendSetOnce(int id, int rpm)
        {
            if (_port == null || !_port.IsOpen) { Log("Port not open"); return; }
            try
            {
                // 단발 송신 + 0x01(TargetRPM) 확인, 실패 시 소수회 재시도
                var ok = SendSetWithConfirm(id, rpm, retries: 3, timeoutMs: 60, backoffMs: 10);
                if (!ok) Log($"[WARN] Set ID={id} RPM={rpm} : 0x01 응답 미수신");
            }
            catch (Exception ex)
            {
                Log($"SetOnce ERR: {ex.Message}");
            }
        }

        private async Task SendLoop(CancellationToken ct)
        {
            var req = new byte[7] { 0x49, 0x53, 0, 0, 0, 0, 0 };
            int originalTimeout = _port!.ReadTimeout;
            _port.ReadTimeout = 50; // ← 더 짧게 설정

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        int id = GetId();
                        if (id == 0) continue;

                        BuildMessageRead(req, id);
                        SendMessage(req);
                        TryReadAndLogOnce();  // 단순 로그/표시 (필요 시 UI 업데이트로 확장)

                        await Task.Delay(120, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Log($"Send ERR: {ex.Message}");
                        await Task.Delay(200, ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _port.ReadTimeout = originalTimeout; // 복구
            }
        }

        // 1) 단발 송신 + 0x01 확인 재시도
        private bool SendSetWithConfirm(int id, int rpm, int retries = 3, int timeoutMs = 60, int backoffMs = 10)
        {
            if (_port == null || !_port.IsOpen) { Log("Port not open"); return false; }

            var req = BuildMessage(id, rpm); // 49 53 id 06 lo hi cs
            int originalTimeout = _port.ReadTimeout;
            _port.ReadTimeout = Math.Min(originalTimeout, Math.Max(10, timeoutMs));

            try
            {
                for (int attempt = 1; attempt <= retries; attempt++)
                {
                    lock (_ioSync)
                    {
                        SendMessage(req); // 내부에서 CS 세팅 + 소량 sleep(GetCommandDelayMs)
                        if (WaitForTargetAck(id, rpm, timeoutMs))
                            return true;
                    }
                    Thread.Sleep(backoffMs);
                }
                Log($"[WARN] Set ID={id} RPM={rpm} : no 0x01 within {timeoutMs}ms x {retries}");
                return false;
            }
            finally
            {
                _port.ReadTimeout = originalTimeout;
            }
        }

        // 2) 0x01(7바이트) 응답 대기/검증
        private bool WaitForTargetAck(int wantId, int wantRpm, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buf = new byte[64];

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    int got = _port!.Read(buf, 0, buf.Length);
                    if (isCommLogging) logger.LogComm("RX", buf, got);
                    if (got >= 7 && buf[0] == 0x44 && buf[1] == 0x54) // 'D','T'
                    {
                        byte id = buf[2];
                        byte cmd = buf[3];

                        // 우선 0x01 7B 확인
                        if (cmd == 0x01 && buf[6] == SumChecksum(buf, 6))
                        {
                            int rpm = DecodeRpmBE(buf[4], buf[5]); // BE
                            if (id == wantId && rpm == wantRpm)
                            {
                                if (isCommLogging) logger.LogResponse(id, 7, 7, error: false);
                                Log($"< DT ID={id} CMD=0x01 TargetRPM={rpm} [{Hex(buf.AsSpan(0, 7))}]");
                                return true;
                            }
                        }

                        // 그 외 프레임은 기존 파서로 소화(로그용)
                        if (TryParseDt(buf, got, out byte rid, out byte rcmd, out int rrpm, out AlarmFlags alarms))
                        {
                            if (rcmd == 0x05)
                            {
                                bool hasAlarm =
                                    (alarms & (AlarmFlags.OverCurrentOrHall |
                                               AlarmFlags.RpmErrorHigher |
                                               AlarmFlags.RpmErrorLower |
                                               AlarmFlags.PtcError |
                                               AlarmFlags.PowerDetectError |
                                               AlarmFlags.IpmOverheat |
                                               AlarmFlags.AbnormalAnyAlarm)) != 0;
                                bool isRemote = !alarms.HasFlag(AlarmFlags.LocalMode);
                                string desc = hasAlarm ? string.Join(", ", DescribeAlarms(alarms)) : "NONE";
                                if (isCommLogging) logger.LogResponse(rid, 7, got, error: hasAlarm);
                                Log($"< DT ID={rid} CMD=0x{rcmd:X2} RPM={rrpm} {(isRemote ? "[REMOTE]" : "[LOCAL]")} ALARM={desc} [{Hex(buf.AsSpan(0, got))}]");
                            }
                            else if (rcmd == 0x01)
                            {
                                if (isCommLogging) logger.LogResponse(rid, 7, got, error: false);
                                Log($"< DT ID={rid} CMD=0x01 TargetRPM={rrpm} [{Hex(buf.AsSpan(0, got))}]");
                                if (rid == wantId && rrpm == wantRpm) return true;
                            }
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // 계속 대기
                }
            }
            return false;
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
        // RPM 인코딩/디코딩 (빅 엔디언)
        static (byte hi, byte lo) EncodeRpmBE(int rpm)
        {
            if (rpm < 0) rpm = 0;
            if (rpm > MAXRPM) rpm = MAXRPM;
            return ((byte)((rpm >> 8) & 0xFF), (byte)(rpm & 0xFF));
        }
        static int DecodeRpmBE(byte hi, byte lo) => (hi << 8) | lo;

        private static IEnumerable<string> DescribeAlarms(AlarmFlags flags)
        {
            foreach (var kv in AlarmDictionary.Description)
            {
                if (kv.Key == AlarmFlags.LocalMode) continue;
                if (flags.HasFlag(kv.Key)) yield return kv.Value;
            }
        }
        static byte SumChecksum(ReadOnlySpan<byte> frame)
        {
            int sum = 0;
            for (int i = 0; i < frame.Length; i++) sum += frame[i];
            return (byte)(sum & 0xFF);
        }
        static byte SumChecksum(byte[] frame, int len)
            => SumChecksum(frame.AsSpan(0, len));
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
        private void AdjustAndSend(int id, int delta)
        {
            var rpm = Math.Clamp(GetCurrentRpm(id) + delta, 0, MAXRPM);
            if (FindName($"TxtSetRpm{id}") is TextBox tb) tb.Text = rpm.ToString();
            SendSetOnce(id, rpm);
        }
        private int GetId()
        {
            lock (_ioSync)
            {
                if (_ids.Count == 0) return 0;
                return _ids[_cursor++ % _ids.Count];
            }
        }
        private static byte[] BuildMessage(int id, int rpm)
        {
            rpm = Math.Clamp(rpm, 0, MAXRPM);
            var (hi, lo) = EncodeRpmBE(rpm);
            return new byte[7] { 0x49, 0x53, (byte)id, 0x06, hi, lo, 0 };
        }
        private static void BuildMessageRead(byte[] req, int id)
        {
            req[2] = (byte)id;
            req[3] = 0x05; req[4] = 0x00; req[5] = 0x00;
            req[6] = SumChecksum(req, 6);
        }
        private int GetCommandDelayMs()
        {
            return 10;
        }
        /// <summary>
        /// RS485 Polling Delay 계산
        /// </summary>
        /// <param name="baudrate">통신 속도 (bps)</param>
        /// <param name="slaveCount">슬레이브 개수</param>
        /// <param name="frameBytes">Request/Response 프레임 크기 (바이트)</param>
        /// <param name="processingDelayMs">Slave 처리 지연 (ms, 기본 30)</param>
        /// <returns>총 Polling Delay(ms)</returns>
        public static double CalculatePollingDelay(int baudrate, int slaveCount, int frameBytes, double processingDelayMs = 30)
        {
            // 1바이트 전송 시간 (ms)
            double byteTimeMs = (1000.0 * 10) / baudrate;

            // Request + Response 전송 시간
            double frameTimeMs = frameBytes * byteTimeMs * 2;

            // Slave 1대 총 소요 시간
            double perSlaveTimeMs = frameTimeMs + processingDelayMs;

            // 전체 Slave 순환 시간
            return perSlaveTimeMs * slaveCount;
        }


        private void SendMessage(byte[] req)
        {
            if (isCommLogging) logger.StartRequest();
            req[6] = SumChecksum(req, 6);
            _port!.Write(req, 0, req.Length);
            if (isCommLogging) logger.LogComm("TX", req, req.Length); // 송신 로그
            Thread.Sleep(GetCommandDelayMs()); // Command 구간 시간 적용
            Log($"> {Hex(req)}");
        }

        private void TryReadAndLogOnce()
        {
            try
            {
                var buf = new byte[32];
                int got = _port!.Read(buf, 0, buf.Length);
                if (isCommLogging) logger.LogComm("RX", buf, got); // 수신 로그

                int offset = 0;
                while (offset < got)
                {
                    // 프레임 헤더 검사
                    if (got - offset >= 7 && buf[offset] == 0x44 && buf[offset + 1] == 0x54)
                    {
                        int frameLen = (buf[offset + 3] == 0x01) ? 7 : ((got - offset >= 9 && buf[offset + 3] == 0x05) ? 9 : 0);
                        if (frameLen > 0 && got - offset >= frameLen)
                        {
                            // 프레임 단위 파싱
                            if (TryParseDt(buf.Skip(offset).ToArray(), frameLen, out byte rid, out byte cmd, out int rrpm, out AlarmFlags alarms))
                            {
                                if (cmd == 0x05)
                                {
                                    bool hasAlarm =
                                        (alarms & (AlarmFlags.OverCurrentOrHall |
                                                   AlarmFlags.RpmErrorHigher |
                                                   AlarmFlags.RpmErrorLower |
                                                   AlarmFlags.PtcError |
                                                   AlarmFlags.PowerDetectError |
                                                   AlarmFlags.IpmOverheat |
                                                   AlarmFlags.AbnormalAnyAlarm)) != 0;
                                    bool isRemote = !alarms.HasFlag(AlarmFlags.LocalMode);
                                    string desc = hasAlarm ? string.Join(", ", DescribeAlarms(alarms)) : "NONE";
                                    if (isCommLogging) logger.LogResponse(rid, 7, frameLen, error: hasAlarm);
                                    Log($"< DT ID={rid} CMD=0x{cmd:X2} RPM={rrpm} {(isRemote ? "[REMOTE]" : "[LOCAL]")} ALARM={desc} [{Hex(buf.AsSpan(offset, frameLen))}]");
                                    UpdateRpmReadUI(rid, rrpm);
                                }
                                else if (cmd == 0x01)
                                {
                                    if (isCommLogging) logger.LogResponse(rid, 7, frameLen, error: false);
                                    Log($"< DT ID={rid} CMD=0x{cmd:X2} TargetRPM={rrpm} [{Hex(buf.AsSpan(offset, frameLen))}]");
                                }
                            }
                            offset += frameLen;
                            continue;
                        }
                    }
                    offset++;
                }

                // 남은 바이트가 프레임이 아니면 Hex로 출력
                if (got > 0 && offset < got)
                {
                    Log($"< {Hex(buf.AsSpan(offset, got - offset))}");
                }
            }
            catch (TimeoutException)
            {
                if (isCommLogging) logger.LogTimeout(0, 7);
            }
        }
        private bool TryParseDt(byte[] buf, int got, out byte id, out byte cmd, out int rpm, out AlarmFlags alarms)
        {
            id = 0; cmd = 0; rpm = 0; alarms = AlarmFlags.None;

            if (got < 7) return false;
            if (buf[0] != 0x44 || buf[1] != 0x54) return false; // 'D','T'

            id = buf[2];
            cmd = buf[3];

            // 공통: RPM = DATA1,DATA2 (LE)
            rpm = DecodeRpmBE(buf[4], buf[5]);

            if (cmd == 0x01)
            {
                // 7바이트 응답: CS = sum(0..5)
                if (buf[6] != SumChecksum(buf, 6)) return false;
                alarms = AlarmFlags.None; // 0x01은 알람 없음
                return true;
            }
            else if (cmd == 0x05)
            {
                // 9바이트 응답 필요
                if (got < 9) return false;
                if (buf[8] != SumChecksum(buf, 8)) return false;

                ushort alarmWord = (ushort)(buf[6] | (buf[7] << 8));
                alarms = (AlarmFlags)alarmWord;
                return true;
            }

            return false;
        }

        #endregion

        private int GetCurrentRpm(int id)
        {
            if (_perTarget.TryGetValue(id, out var v)) return v;
            // TextBox를 이름으로 찾아서 파싱 (초기 0)
            var tb = (TextBox?)FindName($"TxtSetRpm{id}");
            if (tb != null && int.TryParse(tb.Text, out var t)) return t;
            return 0;
        }

        private void Cleanup()
        {
            try
            {
                // 포트가 열려 있으면 닫기 전에 RPM 캐시 저장
                if (_port != null && _port.IsOpen)
                {
                    SaveRpmCache(_port.PortName);
                }
                _cts?.Cancel(); _sendTask?.Wait(200);
            }
            catch { }
            try { _port?.Close(); _port?.Dispose(); } catch { }
            _cts = null; _sendTask = null; _port = null;
            BtnOpen.IsEnabled = true; BtnClose.IsEnabled = false; BtnStart.IsEnabled = false; BtnStop.IsEnabled = false;
            Log("CLOSED");
        }

        private static bool TryGetId(object? tag, out int id)
        {
            if (tag is int v) { id = v; return true; }
            if (int.TryParse(tag?.ToString(), out v)) { id = v; return true; }
            id = 0; return false;
        }

        // 실제 UI 반영 함수 추가
        private void UpdateRpmReadUI(int id, int rpm)
        {
            // 1~6번만 반영 (TextBox 이름: TxtCurRpm1 ~ TxtCurRpm6)
            if (id >= 1 && id <= 6)
            {
                Dispatcher.Invoke(() =>
                {
                    if (FindName($"TxtCurRpm{id}") is TextBox tb)
                        tb.Text = rpm.ToString();
                });
            }
        }

        private void SaveRpmCache(string port)
        {
            var rpmById = new Dictionary<int, int>();
            for (int id = 1; id <= 6; id++)
            {
                if (FindName($"TxtSetRpm{id}") is TextBox tb && int.TryParse(tb.Text, out int rpm))
                    rpmById[id] = rpm;
            }
            var obj = new { port, rpmById, ts = DateTime.Now };
            File.WriteAllText($"rpm_{port}.json", JsonSerializer.Serialize(obj));
        }

        public enum FfuStatus { None, Good, Error }

        private void SetStatusCircle(int id, FfuStatus status)
        {
            if (id < 1 || id > 6) return;
            var ellipse = FindName($"StatusCircle{id}") as System.Windows.Shapes.Ellipse;
            if (ellipse == null) return;
            switch (status)
            {
                case FfuStatus.Good:  ellipse.Fill = System.Windows.Media.Brushes.LimeGreen; break;
                case FfuStatus.Error: ellipse.Fill = System.Windows.Media.Brushes.Red; break;
                case FfuStatus.None:  ellipse.Fill = System.Windows.Media.Brushes.Gray; break;
            }
        }
    }
}
