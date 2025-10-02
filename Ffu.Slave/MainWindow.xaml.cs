using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Ffu.Slave
{
    public partial class MainWindow : Window
    {
        private const int MAXRPM = 2000;

        // 포트 컨텍스트(배열/리스트로 관리)
        private sealed class PortCtx
        {
            public string Name = "";
            public SerialPort Port = null!;
            public CancellationTokenSource Cts = null!;
            public Task LoopTask = null!;
            public readonly List<byte> Rx = new List<byte>(64);
        }

        private readonly List<PortCtx> _ports = new List<PortCtx>();

        private HashSet<int> _idSet = new();
        public ObservableCollection<WatchSlot> WatchSlots { get; } = new();

        // ID→RPM 캐시(수신값 저장)
        private readonly ConcurrentDictionary<int, int> _rpmById = new();

        // 통신 로깅
        private bool isCommLogging = true;
        private CommLogger logger = new CommLogger("rs485_slave_log.csv");

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
                // 여러 포트 입력 허용: 콤마/세미콜론/공백
                var tokens = TxtCom.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (tokens.Length == 0) { Log("COM 입력 없음"); return; }

                foreach (var raw in tokens)
                {
                    var com = raw.Trim();
                    if (string.IsNullOrWhiteSpace(com)) continue;

                    // 이미 열려 있는지 단순 확인
                    bool already = false;
                    for (int i = 0; i < _ports.Count; i++)
                    {
                        if (string.Equals(_ports[i].Name, com, StringComparison.OrdinalIgnoreCase))
                        {
                            already = true; break;
                        }
                    }
                    if (already)
                    {
                        Log($"{com} 이미 열림"); continue;

                    }

                    var sp = new SerialPort(com, 9600, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout = 200,
                        WriteTimeout = 200
                    };
                    sp.Open();

                    var ctx = new PortCtx { Name = sp.PortName, Port = sp };
                    _ports.Add(ctx);

                    Log($"OPEN {sp.PortName} 9600-8N1");
                }

                if (_ports.Count > 0)
                {
                    BtnOpen.IsEnabled = false; BtnClose.IsEnabled = true; BtnStart.IsEnabled = true;
                }
            }
            catch (Exception ex) { Log($"Open FAIL: {ex.Message}"); }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Cleanup();

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_ports.Count == 0)
            {
                Log("Port not open");
                return;
            }

            var ids = ParseIdSet(TxtIds.Text);
            if (ids.Count == 0) { Log("IDs empty or invalid. ex) 1,3,5-7"); return; }

            _idSet = new HashSet<int>(ids);

            // 포트별 루프 시작
            for (int i = 0; i < _ports.Count; i++)
            {
                var ctx = _ports[i];
                if (!ctx.Port.IsOpen) continue;

                ctx.Cts = new CancellationTokenSource();
                ctx.LoopTask = Task.Run(() => ListenLoop(ctx, ctx.Cts.Token));
            }

            BtnStart.IsEnabled = false; BtnStop.IsEnabled = true;
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < _ports.Count; i++)
            {
                try { _ports[i].Cts?.Cancel(); } catch { }
            }
            BtnStart.IsEnabled = true; BtnStop.IsEnabled = false;
        }

        private static List<int> ParseIdSet(string text)
        {
            var set = new SortedSet<int>();
            if (string.IsNullOrWhiteSpace(text)) return new List<int>();

            foreach (var tok in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.Contains("-"))
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

        // BE 인코딩/디코딩
        static (byte hi, byte lo) EncodeRpmBE(int rpm)
        {
            if (rpm < 0) rpm = 0;
            if (rpm > MAXRPM) rpm = MAXRPM;
            return ((byte)((rpm >> 8) & 0xFF), (byte)(rpm & 0xFF));
        }
        static int DecodeRpmBE(byte hi, byte lo) => (hi << 8) | lo;

        private async Task ListenLoop(PortCtx ctx, CancellationToken ct)
        {
            var port = ctx.Port;
            var rx = ctx.Rx;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int b = port.ReadByte(); // ReadTimeout=200ms
                    if (b < 0) continue;
                    rx.Add((byte)b);

                    // 헤더 동기화: 49 53 ...
                    while (rx.Count >= 1 && rx[0] != 0x49) rx.RemoveAt(0);
                    if (rx.Count >= 2 && rx[1] != 0x53) { rx.RemoveAt(0); continue; }

                    // 요청 프레임: 49 53 ID CMD ... CS (총 7바이트)
                    if (rx.Count >= 7)
                    {
                        byte id = rx[2];
                        byte cmd = rx[3];
                        byte cs = rx[6];
                        byte calc = SumChecksum(rx, 0, 6);   // CS 제외(0~5) 합의 LSB

                        if (cs == calc && _idSet.Contains(id))
                        {
                            switch (cmd)
                            {
                                case 0x06: // SET Target RPM, 7바이트
                                    {
                                        if (isCommLogging) logger.StartRequest();

                                        byte hi = rx[4], lo = rx[5];
                                        int rpm = DecodeRpmBE(hi, lo);
                                        if (rpm < 0) rpm = 0; if (rpm > MAXRPM) rpm = MAXRPM;

                                        _rpmById[id] = rpm;
                                        UpdateWatchSlotsFor(id, rpm);
                                        Log($"[{ctx.Name}] SET ID={id} RPM={rpm}");

                                        // ACK 미전송 (요구사항 준수)
                                        // Target RPM 반환: 0x01, 7바이트
                                        var resp = new byte[7];
                                        resp[0] = 0x44; resp[1] = 0x54; resp[2] = id;
                                        resp[3] = 0x01;
                                        resp[4] = hi; resp[5] = lo;
                                        resp[6] = SumChecksum(resp, 6);

                                        int delayMs = Random.Shared.Next(2, 31);
                                        await Task.Delay(delayMs, ct);
                                        port.Write(resp, 0, resp.Length);
                                        if (isCommLogging) logger.LogResponse(id, 7, resp.Length, error: false);

                                        Log($"[{ctx.Name}] RES(TargetRPM) {Hex(resp)}");
                                        rx.RemoveRange(0, 7);
                                        continue;
                                    }
                                case 0x05: // GET Current RPM, 7바이트
                                    {
                                        if (isCommLogging) logger.StartRequest();
                                        Log($"[{ctx.Name}] REQ {Hex(rx, 0, 7)}");

                                        int rpm = _rpmById.TryGetValue(id, out var r) ? r : 0;
                                        _rpmById[id] = rpm;
                                        UpdateWatchSlotsFor(id, rpm);
                                        var (hi, lo) = EncodeRpmBE(rpm);

                                        var resp = new byte[9];
                                        resp[0] = 0x44; resp[1] = 0x54; resp[2] = id;
                                        resp[3] = 0x05; resp[4] = hi; resp[5] = lo;

                                        // 상태 바이트(예: 0x0100 또는 0x0000) 랜덤
                                        if (Random.Shared.NextDouble() < 0.5) { resp[6] = 0x01; resp[7] = 0x00; }
                                        else { resp[6] = 0x00; resp[7] = 0x00; }

                                        resp[8] = SumChecksum(resp, 8);

                                        int delayMs = Random.Shared.Next(2, 31);
                                        await Task.Delay(delayMs, ct);
                                        port.Write(resp, 0, resp.Length);
                                        if (isCommLogging) logger.LogResponse(id, 7, 9, error: false);

                                        Log($"[{ctx.Name}] RES {Hex(resp)}");
                                        rx.RemoveRange(0, 7);
                                        continue;
                                    }
                                default:
                                    {
                                        // 알 수 없는 명령은 버퍼만 밀어냄
                                        Log($"[{ctx.Name}] Unknown CMD=0x{cmd:X2}");
                                        rx.RemoveRange(0, 7);
                                        continue;
                                    }
                            }
                        }

                        // 체크섬 불일치/대상 ID 아님 → 헤더 다음으로 진행
                        rx.RemoveRange(0, 7);
                    }
                }
                catch (TimeoutException)
                {
                    if (isCommLogging) logger.LogTimeout(0, 0);
                    // 필요시 타임아웃 로그를 포트별로:
                    // Log($"[{ctx.Name}] Timeout");
                }
                catch (Exception ex)
                {
                    Log($"[{ctx.Name}] Loop ERR: {ex.Message}");
                    await Task.Delay(50, ct);
                }
            }
        }

        private void UpdateWatchSlotsFor(int id, int rpm)
        {
            Dispatcher.BeginInvoke(() =>
            {
                for (int i = 0; i < WatchSlots.Count; i++)
                {
                    var slot = WatchSlots[i];
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

        // HEX 도우미
        static string Hex(List<byte> src, int offset, int length)
        {
            var sb = new StringBuilder(length * 3);
            for (int i = 0; i < length; i++) sb.Append(src[offset + i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }
        static string Hex(byte[] buf)
        {
            var sb = new StringBuilder(buf.Length * 3);
            for (int i = 0; i < buf.Length; i++) sb.Append(buf[i].ToString("X2")).Append(' ');
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
            try
            {
                // 중지 요청
                for (int i = 0; i < _ports.Count; i++)
                {
                    try { _ports[i].Cts?.Cancel(); } catch { }
                }
                // 종료 대기
                for (int i = 0; i < _ports.Count; i++)
                {
                    try { _ports[i].LoopTask?.Wait(200); } catch { }
                }
                // 포트 닫기
                for (int i = 0; i < _ports.Count; i++)
                {
                    try { _ports[i].Port?.Close(); _ports[i].Port?.Dispose(); } catch { }
                }
            }
            catch { }
            finally
            {
                _ports.Clear();
            }

            BtnOpen.IsEnabled = true; BtnClose.IsEnabled = false; BtnStart.IsEnabled = false; BtnStop.IsEnabled = false;
            Log("CLOSED ALL");
        }

        private void Log(string s)
        {
            Dispatcher.Invoke(() => { TxtLog.AppendText(s + Environment.NewLine); TxtLog.ScrollToEnd(); });
        }
    }
}
