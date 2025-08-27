using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ffu.Master
{
    class Checksum
    {
        static byte SumChecksum(ReadOnlySpan<byte> frame)
        {
            int sum = 0;
            for (int i = 0; i < frame.Length; i++) sum += frame[i];
            return (byte)(sum & 0xFF);
        }

    }
}
