using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Ffu.Master
{
    public partial class MainWindow
    {
        private const string IDS_CACHE_FILE = "ids.json";

        // 포트 오픈 성공 직후 한 번 호출
        private void ScanSlaveID()
        {
            try
            {
                var cached = LoadIdsCache();
                if (cached is { } c && string.Equals(c.port, _port!.PortName, StringComparison.OrdinalIgnoreCase))
                {
                    TxtIds.Text = string.Join(",", c.ids);
                    Log($"[DISC] load cache: {TxtIds.Text}");
                }
                else
                {
                    //초기 1~64 스캔 딜레이는 실제 환경에서 테스트하여 조정 필요 250903 smlee
                    //var ids = SlaveID_ScanSync(start: 1, end: 64 
                    var ids = SlaveID_ScanSync(start: 1, end: 6, retries: 1, interDelayMs: 8, readTimeoutOverride: 100);
                    TxtIds.Text = string.Join(",", ids);
                    SaveIdsCache(_port!.PortName, ids);
                    Log($"[DISC] scan complete: {TxtIds.Text}");
                }
            }
            catch (Exception ex)
            {
                Log($"[DISC] {ex.Message}");
            }
        }

        // 동기 스캔: READ(0x05)로 1..N 탐색 → 응답 있으면 활성
        private List<int> SlaveID_ScanSync(int start, int end, int retries, int interDelayMs, int? readTimeoutOverride)
        {
            var found = new List<int>();
            if (_port == null || !_port.IsOpen) return found;

            int backupTimeout = _port.ReadTimeout;
            if (readTimeoutOverride.HasValue) _port.ReadTimeout = readTimeoutOverride.Value;

            try
            {
                for (int id = start; id <= end; id++)
                {
                    bool ok = false;
                    for (int attempt = 0; attempt <= retries && !ok; attempt++)
                    {
                        var req = BuildReadReq(id);
                        try
                        {
                            // 루프/수동 TX와 충돌 방지
                            lock (_ioSync)
                            {
                                _port.DiscardInBuffer();
                                _port.Write(req, 0, req.Length);

                                var buf = new byte[32];
                                int got = _port.Read(buf, 0, buf.Length); // ReadTimeout 사용
                                ok = IsReadAck(buf, got, id);
                            }
                        }
                        catch (TimeoutException) { /* no-op */ }
                        catch { /* no-op */ }

                        if (!ok) Thread.Sleep(2); // 소폭 간격
                    }

                    if (ok)
                    {
                        found.Add(id);
                        Log($"[DISC] present: {id}");
                    }

                    if (interDelayMs > 0) Thread.Sleep(interDelayMs);
                }
            }
            finally
            {
                if (readTimeoutOverride.HasValue) _port.ReadTimeout = backupTimeout;
            }
            return found;
        }

        // 'I','S', id, 0x05, 0,0, CS
        private static byte[] BuildReadReq(int id)
        {
            var req = new byte[7] { 0x49, 0x53, (byte)id, 0x05, 0x00, 0x00, 0x00 };
            req[6] = SumChecksum(req, 6); // ← MainWindow에 이미 있는 헬퍼를 재사용
            return req;
        }

        // 'D','T', id, 0x05, ..., CS OK
        private static bool IsReadAck(byte[] buf, int got, int id)
        {
            if (got < 9) return false;
            if (buf[0] != 0x44 || buf[1] != 0x54) return false; // DT
            if (buf[2] != (byte)id) return false;               // ID 매칭
            if (buf[3] != 0x05) return false;                   // READ 응답
            return buf[8] == SumChecksum(buf, 8);               // 체크섬
        }

        private void SaveIdsCache(string port, IEnumerable<int> ids)
        {
            var obj = new { port, ids = ids.ToArray(), ts = DateTime.Now };
            File.WriteAllText(IDS_CACHE_FILE, JsonSerializer.Serialize(obj));
        }

        private (string port, int[] ids)? LoadIdsCache()
        {
            if (!File.Exists(IDS_CACHE_FILE)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(IDS_CACHE_FILE));
            var root = doc.RootElement;
            var port = root.GetProperty("port").GetString() ?? "";
            var ids = root.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            return (port, ids);
        }
}
}
