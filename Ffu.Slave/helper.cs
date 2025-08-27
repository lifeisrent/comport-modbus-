// Helpers.cs
using System;
using System.Collections.Generic;
using System.Linq;

static class FfuHelpers
{
    // "1,3,5-7" → [1,3,5,6,7] (1~64만)
    public static List<int> ParseIdSet(string text)
    {
        var set = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(text)) return set.ToList();

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
        return set.ToList();
    }

    public static byte SumChecksum(byte[] frame, int len /*exclude CS*/)
    {
        int sum = 0;
        for (int i = 0; i < len; i++) sum += frame[i];
        return (byte)(sum & 0xFF);
    }

    public static string Hex(ReadOnlySpan<byte> span)
    {
        var chars = new char[span.Length * 3];
        int k = 0;
        for (int i = 0; i < span.Length; i++)
        {
            var s = span[i].ToString("X2");
            chars[k++] = s[0]; chars[k++] = s[1]; chars[k++] = ' ';
        }
        return new string(chars, 0, Math.Max(0, k - 1));
    }
}
