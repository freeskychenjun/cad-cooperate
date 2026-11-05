// PipeIo.cs - named-pipe message framing for the resident daemon (cad serve / daemon).
//
// Moved verbatim out of CadHelper.cs (plan 5.1 batch 1: Json + PipeIo extraction). The class body
// is a pure move: the wire format and the size guard must not change.
//
// Wire format that must not change (scripts\client\CadClient.cs and scripts\cad.ps1 rely on it):
//   * 4-byte little-endian length prefix (BitConverter / x86 layout),
//   * then exactly `len` bytes of UTF-8 JSON,
//   * a length <= 0 or > 64 MiB, a short header or a short payload means "no message" (null).

using System;
using System.IO;
using System.Text;

namespace CadHelper
{
    internal static class PipeIo
    {
        public static string ReadMessage(Stream s)
        {
            byte[] hdr = new byte[4];
            if (!ReadFully(s, hdr, 4)) return null;
            int len = BitConverter.ToInt32(hdr, 0);
            if (len <= 0 || len > 64 * 1024 * 1024) return null;
            byte[] buf = new byte[len];
            if (!ReadFully(s, buf, len)) return null;
            return Encoding.UTF8.GetString(buf);
        }

        public static void WriteMessage(Stream s, string text)
        {
            byte[] buf = Encoding.UTF8.GetBytes(text ?? "");
            byte[] hdr = BitConverter.GetBytes(buf.Length);
            s.Write(hdr, 0, 4);
            s.Write(buf, 0, buf.Length);
            s.Flush();
        }

        private static bool ReadFully(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }
}
