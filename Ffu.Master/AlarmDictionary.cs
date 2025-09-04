using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ffu.Master
{
    /// <summary>
    /// FFU Alarm Status (2바이트, HIGH: 상위 1바이트 / LOW: 하위 1바이트)
    /// 비트 맵은 스펙 표 그대로 반영.
    /// </summary>
    [Flags]
    public enum AlarmFlags : ushort
    {
        None = 0x0000,

        // LOW byte (bit0~bit7)
        OverCurrentOrHall = 0x0001, // bit0: 과전류 or 홀센서 오류
        RpmErrorHigher = 0x0002, // bit1: 현재 RPM > 설정 RPM
        RpmErrorLower = 0x0004, // bit2: 현재 RPM < 설정 RPM
        PtcError = 0x0008, // bit3: 모터 과열 or 케이블 미연결
        // bit4~bit7 (LOW의 4~7)은 스펙에 별도 기술 없으면 Reserved로 둠
        LowReserved4 = 0x0010,
        LowReserved5 = 0x0020,
        LowReserved6 = 0x0040,
        LowReserved7 = 0x0080,

        // HIGH byte (bit8~bit15) - 표의 의미 반영
        AbnormalAnyAlarm = 0x1000, // HIGH bit4: 어떤 알람이든 발생 시 Set
        IpmOverheat = 0x2000, // HIGH bit5: IPM 과열
        PowerDetectError = 0x4000, // HIGH bit6: 입력전원(저전압 등) 오류
        LocalMode = 0x8000, // HIGH bit7: 1=로컬모드, 0=리모트모드

        // HIGH bit0~bit3은 스펙 없으면 Reserved
        HighReserved0 = 0x0100,
        HighReserved1 = 0x0200,
        HighReserved2 = 0x0400,
        HighReserved3 = 0x0800,
    }

    public static class AlarmDictionary
    {
        public static readonly IReadOnlyDictionary<AlarmFlags, string> Description =
            new Dictionary<AlarmFlags, string>
            {
                { AlarmFlags.OverCurrentOrHall,  "과전류 또는 홀센서 알람" },
                { AlarmFlags.RpmErrorHigher,     "RPM 편차 알람: 현재 RPM > 설정 RPM" },
                { AlarmFlags.RpmErrorLower,      "RPM 편차 알람: 현재 RPM < 설정 RPM" },
                { AlarmFlags.PtcError,           "PTC/모터 과열 또는 모터 케이블 미연결" },
                { AlarmFlags.AbnormalAnyAlarm,   "알람 발생 상태(집계 플래그)" },
                { AlarmFlags.IpmOverheat,        "IPM(내부부품) 과열" },
                { AlarmFlags.PowerDetectError,   "입력전원 저전압/전원 오류" },
                { AlarmFlags.LocalMode,          "로컬 모드(= 리모트 아님)" },
            };
    }
}
