using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ScsManifestRemover
{
    internal sealed class PatchResult
    {
        public int PatchedEntries;
        public readonly List<string> Log = new List<string>();
    }

    /// <summary>
    /// Hides a file inside a HashFS (.scs) archive without extracting it.
    /// The game looks files up by the CityHash64 of their path, so changing the
    /// stored hash of an entry to hash+1 makes the file invisible while keeping
    /// the entry table sorted. File data is left untouched.
    /// </summary>
    internal static class HashFsPatcher
    {
        private const uint Magic = 0x23534353; // "SCS#"

        public static ulong HashPath(string path, ushort salt)
        {
            if (path.StartsWith("/")) path = path.Substring(1);
            if (salt != 0) path = salt.ToString() + path;
            byte[] bytes = Encoding.UTF8.GetBytes(path);
            return CityHash.CityHash64(bytes, (ulong)bytes.Length);
        }

        public static PatchResult Patch(string file, string target, bool dryRun)
        {
            var result = new PatchResult();
            var access = dryRun ? FileAccess.Read : FileAccess.ReadWrite;
            var share = dryRun ? FileShare.Read : FileShare.None;

            using (var fs = new FileStream(file, FileMode.Open, access, share))
            {
                byte[] hdr = ReadAt(fs, 0, 52);
                if (hdr == null || BitConverter.ToUInt32(hdr, 0) != Magic)
                {
                    if (hdr != null && hdr[0] == 'P' && hdr[1] == 'K')
                        throw new InvalidDataException("This is a ZIP archive, not HashFS.");
                    throw new InvalidDataException("Not a HashFS archive (missing SCS# signature).");
                }

                ushort version = BitConverter.ToUInt16(hdr, 4);
                ushort salt = BitConverter.ToUInt16(hdr, 6);
                string method = Encoding.ASCII.GetString(hdr, 8, 4);
                uint numEntries = BitConverter.ToUInt32(hdr, 12);
                result.Log.Add(string.Format("HashFS v{0}, salt {1}, hash {2}, {3} entries", version, salt, method, numEntries));

                if (method != "CITY")
                    throw new NotSupportedException("Unsupported hash method: " + method);

                // The salt is prepended to the path before hashing. Locked archives sometimes
                // set a salt, so try both the salted and the plain hash.
                var targets = new List<ulong> { HashPath(target, salt) };
                if (salt != 0) targets.Add(HashPath(target, 0));
                foreach (ulong t in targets)
                    result.Log.Add(string.Format("Looking for '{0}': {1:X16}", target, t));

                if (version == 1)
                    PatchV1(fs, hdr, numEntries, targets, dryRun, result);
                else if (version == 2)
                    PatchV2(fs, hdr, numEntries, targets, dryRun, result);
                else
                    throw new NotSupportedException("Unsupported HashFS version: " + version);

                if (!dryRun && result.PatchedEntries > 0)
                    fs.Flush(true);
            }
            return result;
        }

        // v1: uncompressed table of 32-byte entries; hash is the first 8 bytes.
        private static void PatchV1(FileStream fs, byte[] hdr, uint numEntries, List<ulong> targets,
            bool dryRun, PatchResult result)
        {
            long tableLen = (long)numEntries * 32;
            // Some locked archives point the header at garbage; the table then sits at the end.
            long[] starts = { (long)BitConverter.ToUInt64(hdr, 16), fs.Length - tableLen };
            foreach (long start in starts)
            {
                byte[] table = ReadAt(fs, start, (int)tableLen);
                if (table == null) continue;
                int patched = PatchTable(table, 32, (int)numEntries, targets, result);
                if (patched == 0) continue;

                if (!dryRun)
                {
                    WriteAt(fs, start, table);
                    result.Log.Add("Entry table written in place at offset " + start + ".");
                }
                result.PatchedEntries = patched;
                return;
            }
        }

        // v2: zlib-compressed table of 16-byte entries; hash is the first 8 bytes.
        private static void PatchV2(FileStream fs, byte[] hdr, uint numEntries, List<ulong> targets,
            bool dryRun, PatchResult result)
        {
            uint etLen = BitConverter.ToUInt32(hdr, 16);
            long etStart = (long)BitConverter.ToUInt64(hdr, 28);
            uint securityDescriptor = BitConverter.ToUInt32(hdr, 44);
            if (securityDescriptor != 0)
                result.Log.Add("WARNING: archive has a security descriptor (" + securityDescriptor + ").");

            byte[] compressed = ReadAt(fs, etStart, (int)etLen);
            if (compressed == null)
                throw new InvalidDataException("Entry table lies outside the file.");

            byte[] table = ZlibDecompress(compressed);
            int count = table.Length / 16;
            if (count != numEntries)
                result.Log.Add("WARNING: table holds " + count + " entries, header says " + numEntries + ".");

            int patched = PatchTable(table, 16, count, targets, result);
            if (patched == 0) return;
            result.PatchedEntries = patched;
            if (dryRun) return;

            byte[] newCompressed = ZlibCompress(table);
            if (!BytesEqual(ZlibDecompress(newCompressed), table))
                throw new InvalidDataException("Recompressed table failed verification; file not changed.");

            // Write in place if it fits, otherwise append and repoint the header.
            long newStart = newCompressed.Length <= etLen ? etStart : fs.Length;
            WriteAt(fs, newStart, newCompressed);
            WriteAt(fs, 16, BitConverter.GetBytes((uint)newCompressed.Length));
            WriteAt(fs, 28, BitConverter.GetBytes((ulong)newStart));
            result.Log.Add(newStart == etStart
                ? "Entry table written in place at offset " + newStart + "."
                : "Recompressed table is larger; appended at offset " + newStart + ".");
        }

        private static int PatchTable(byte[] table, int entrySize, int count, List<ulong> targets, PatchResult result)
        {
            var existing = new HashSet<ulong>();
            for (int i = 0; i < count; i++)
                existing.Add(BitConverter.ToUInt64(table, i * entrySize));

            int patched = 0;
            for (int i = 0; i < count; i++)
            {
                ulong hash = BitConverter.ToUInt64(table, i * entrySize);
                if (!targets.Contains(hash)) continue;

                ulong newHash = hash + 1;
                if (existing.Contains(newHash))
                {
                    result.Log.Add("Hash collision at entry #" + i + ", skipped.");
                    continue;
                }
                Array.Copy(BitConverter.GetBytes(newHash), 0, table, i * entrySize, 8);
                result.Log.Add(string.Format("Entry #{0}: {1:X16} -> {2:X16}", i, hash, newHash));
                patched++;
            }
            return patched;
        }

        private static byte[] ReadAt(FileStream fs, long pos, int len)
        {
            if (pos < 0 || len < 0 || pos + len > fs.Length) return null;
            var buf = new byte[len];
            fs.Position = pos;
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(buf, read, len - read);
                if (n <= 0) return null;
                read += n;
            }
            return buf;
        }

        private static void WriteAt(FileStream fs, long pos, byte[] data)
        {
            fs.Position = pos;
            fs.Write(data, 0, data.Length);
        }

        private static byte[] ZlibDecompress(byte[] data)
        {
            if (data.Length < 6 || (data[0] & 0x0F) != 8 || ((data[0] << 8) | data[1]) % 31 != 0)
                throw new InvalidDataException("Entry table is not a zlib stream.");
            using (var input = new MemoryStream(data, 2, data.Length - 2))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0xDA);
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                    deflate.Write(data, 0, data.Length);

                uint a = 1, b = 0;
                foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);
                return output.ToArray();
            }
        }

        private static bool BytesEqual(byte[] x, byte[] y)
        {
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
                if (x[i] != y[i]) return false;
            return true;
        }
    }
}
