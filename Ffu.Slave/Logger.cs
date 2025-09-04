using System;
using System.Diagnostics;
using System.IO;

namespace Ffu.Slave
{
    public class CommLogger
    {
        private readonly string logFile;
        private readonly Stopwatch stopwatch;

        public CommLogger(string logPath = "comm_log.csv")
        {
            logFile = logPath;
            stopwatch = new Stopwatch();

            // CSV 헤더 작성
            if (!File.Exists(logFile))
            {
                File.WriteAllText(logFile, "Timestamp,SlaveID,RequestBytes,ResponseBytes,ResponseTimeMs,Timeout,Error\n");
            }
        }

        /// <summary>
        /// 로깅 시작 (Request 전송 시점)
        /// </summary>
        public void StartRequest()
        {
            stopwatch.Restart();
        }

        /// <summary>
        /// 응답 성공 기록
        /// </summary>
        public void LogResponse(int slaveId, int requestBytes, int responseBytes, bool error = false)
        {
            stopwatch.Stop();
            double elapsed = stopwatch.Elapsed.TotalMilliseconds;

            string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{slaveId},{requestBytes},{responseBytes},{elapsed:F2},0,{(error ? 1 : 0)}";
            File.AppendAllText(logFile, logLine + Environment.NewLine);
        }

        /// <summary>
        /// 타임아웃 기록
        /// </summary>
        public void LogTimeout(int slaveId, int requestBytes)
        {
            stopwatch.Stop();
            string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{slaveId},{requestBytes},0,0,1,0";
            File.AppendAllText(logFile, logLine + Environment.NewLine);
        }
    }
}
