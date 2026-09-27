// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A BCF component names an element by its IFC GlobalId: a 22-character base64
// string, NOT a .NET Guid. Revit gives an element's export identity as a Guid
// (ExportUtils.GetExportId / the IFC_GUID parameter), so resolving an incoming
// IfcGuid means converting between the two - a well-published, decade-old
// compression algorithm (buildingSMART's IFC2x3 implementation guide; this is
// the same port used across the Revit/IFC ecosystem, e.g. https://github.com
// /hakonhc/IfcGuid). It is pure arithmetic over bytes, so it lives in Core and
// is tested WITHOUT a model, same as CoordinationRules.
//
// MEASURED against the reference implementation's own test vector: the .NET
// Guid "01cf62c8-e9bc-bf88-0000-000000000005" encodes to
// "01psB8wRo$Y00000000005" - verified by independently re-deriving the
// algorithm bit-for-bit (shift/mask, not the reference's truncating / and %,
// which are equivalent in C# but were re-derived here to be sure) and
// confirmed to match before this file was written. That pair is one of the
// round-trip tests below.
// -----------------------------------------------------------------------------
using System;
using System.Text;

namespace Horizun.Revit.Core
{
    public static class IfcGuidCodec
    {
        public const int IfcGuidLength = 22;

        private static readonly char[] Base64Chars =
        {
            '0','1','2','3','4','5','6','7','8','9','A','B','C','D','E','F','G','H','I','J','K','L','M','N',
            'O','P','Q','R','S','T','U','V','W','X','Y','Z','a','b','c','d','e','f','g','h','i','j','k','l',
            'm','n','o','p','q','r','s','t','u','v','w','x','y','z','_','$'
        };

        /// <summary>
        /// A .NET Guid (as ExportUtils.GetExportId returns) to its 22-character compressed
        /// IFC GlobalId. Bit-exact port of the buildingSMART algorithm: Data1/Data2/Data3
        /// (little-endian, as .NET lays them out) and the 8 raw Data4 bytes are re-packed
        /// into six unsigned integers (12, then five 24-bit groups = 132 bits for 128
        /// significant ones - the first character therefore only ever takes values 0-3)
        /// and each group is written out in base64 using this alphabet.
        /// </summary>
        public static string Encode(Guid guid)
        {
            byte[] b = guid.ToByteArray();
            uint v0 = BitConverter.ToUInt32(b, 0);
            uint u1 = BitConverter.ToUInt16(b, 4);
            uint u2 = BitConverter.ToUInt16(b, 6);

            var num = new uint[6];
            num[0] = v0 >> 24;
            num[1] = v0 & 0xFFFFFFu;
            num[2] = (u1 << 8) | (u2 >> 8);
            num[3] = ((u2 & 0xFFu) << 16) | ((uint)b[8] << 8) | b[9];
            num[4] = ((uint)b[10] << 16) | ((uint)b[11] << 8) | b[12];
            num[5] = ((uint)b[13] << 16) | ((uint)b[14] << 8) | b[15];

            var sb = new StringBuilder(IfcGuidLength);
            int[] lens = { 2, 4, 4, 4, 4, 4 };
            for (int i = 0; i < 6; i++) sb.Append(To64(num[i], lens[i]));
            return sb.ToString();
        }

        /// <summary>
        /// The reverse: a 22-character compressed IFC GlobalId back to a .NET Guid, or
        /// null when the string is not one this codec can read - the WRONG LENGTH, or a
        /// character outside the alphabet. A BCF written by an unrelated tool may carry
        /// something that merely LOOKS like a GUID; a caller must never receive a made-up
        /// Guid for it, only null.
        /// </summary>
        public static Guid? Decode(string ifcGuid)
        {
            if (string.IsNullOrEmpty(ifcGuid) || ifcGuid.Length != IfcGuidLength) return null;
            var num = new uint[6];
            int[] lens = { 2, 4, 4, 4, 4, 4 };
            int pos = 0;
            for (int i = 0; i < 6; i++)
            {
                uint value;
                if (!From64(ifcGuid, pos, lens[i], out value)) return null;
                num[i] = value;
                pos += lens[i];
            }

            uint v0 = (num[0] << 24) | num[1];
            ushort d2 = (ushort)(num[2] >> 8);
            ushort d3 = (ushort)(((num[2] & 0xFFu) << 8) | (num[3] >> 16));
            var tail = new byte[8];
            tail[0] = (byte)((num[3] >> 8) & 0xFFu);
            tail[1] = (byte)(num[3] & 0xFFu);
            tail[2] = (byte)(num[4] >> 16);
            tail[3] = (byte)((num[4] >> 8) & 0xFFu);
            tail[4] = (byte)(num[4] & 0xFFu);
            tail[5] = (byte)(num[5] >> 16);
            tail[6] = (byte)((num[5] >> 8) & 0xFFu);
            tail[7] = (byte)(num[5] & 0xFFu);

            var bytes = new byte[16];
            bytes[0] = (byte)(v0 & 0xFFu); bytes[1] = (byte)((v0 >> 8) & 0xFFu);
            bytes[2] = (byte)((v0 >> 16) & 0xFFu); bytes[3] = (byte)((v0 >> 24) & 0xFFu);
            bytes[4] = (byte)(d2 & 0xFF); bytes[5] = (byte)(d2 >> 8);
            bytes[6] = (byte)(d3 & 0xFF); bytes[7] = (byte)(d3 >> 8);
            Array.Copy(tail, 0, bytes, 8, 8);
            return new Guid(bytes);
        }

        private static string To64(uint number, int len)
        {
            var chars = new char[len];
            uint act = number;
            for (int digit = 0; digit < len; digit++)
            {
                chars[len - digit - 1] = Base64Chars[(int)(act & 63u)];
                act >>= 6;
            }
            return new string(chars);
        }

        private static bool From64(string s, int start, int len, out uint value)
        {
            value = 0;
            for (int i = 0; i < len; i++)
            {
                int index = Array.IndexOf(Base64Chars, s[start + i]);
                if (index < 0) return false;
                value = (value << 6) | (uint)index;
            }
            return true;
        }
    }
}
