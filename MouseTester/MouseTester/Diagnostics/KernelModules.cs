using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseTester.Diagnostics
{
    public class KernelModule
    {
        public ulong ImageBase;
        public uint ImageSize;
        public string FileName;   // basename, e.g. ntoskrnl.exe, USBPORT.SYS, Wdf01000.sys
        public string FullPath;   // device-style, e.g. \SystemRoot\system32\drivers\USBPORT.SYS
    }

    public static class KernelModules
    {
        // Layout of RTL_PROCESS_MODULE_INFORMATION on x64.
        private const int EntrySize = 296;
        private const int OffsetImageBase = 16;       // after Section (8) + MappedBase (8)
        private const int OffsetImageSize = 24;       // ULONG
        private const int OffsetOffsetToFileName = 38; // USHORT, after Flags (4) + 4 USHORTs (8)
        private const int OffsetFullPathName = 40;
        private const int FullPathNameLen = 256;

        public static List<KernelModule> Enumerate()
        {
            var result = new List<KernelModule>();
            uint size = 1024 * 1024;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int status;
                uint returnLength = 0;
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal((int)size);
                    status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemModuleInformation, buffer, size, out returnLength);
                    if (status == 0) break;
                    if (status == unchecked((int)0xC0000004) /* STATUS_INFO_LENGTH_MISMATCH */)
                    {
                        size = Math.Max(returnLength, size * 2);
                        continue;
                    }
                    return result; // unknown error, give up
                }

                uint count = (uint)Marshal.ReadInt32(buffer);
                IntPtr cursor = IntPtr.Add(buffer, 8); // ULONG NumberOfModules + 4 padding on x64
                for (uint i = 0; i < count; i++)
                {
                    var entry = IntPtr.Add(cursor, (int)i * EntrySize);
                    var m = ReadEntry(entry);
                    if (m != null) result.Add(m);
                }
            }
            catch { }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }

            result.Sort((a, b) => a.ImageBase.CompareTo(b.ImageBase));
            return result;
        }

        private static KernelModule ReadEntry(IntPtr entry)
        {
            try
            {
                ulong imageBase = (ulong)Marshal.ReadInt64(entry, OffsetImageBase);
                uint imageSize = (uint)Marshal.ReadInt32(entry, OffsetImageSize);
                ushort offsetToFileName = (ushort)Marshal.ReadInt16(entry, OffsetOffsetToFileName);

                var bytes = new byte[FullPathNameLen];
                Marshal.Copy(IntPtr.Add(entry, OffsetFullPathName), bytes, 0, FullPathNameLen);
                int len = 0;
                while (len < bytes.Length && bytes[len] != 0) len++;
                string fullPath = Encoding.ASCII.GetString(bytes, 0, len);
                string fileName = offsetToFileName < len
                    ? Encoding.ASCII.GetString(bytes, offsetToFileName, len - offsetToFileName)
                    : System.IO.Path.GetFileName(fullPath);

                return new KernelModule
                {
                    ImageBase = imageBase,
                    ImageSize = imageSize,
                    FileName = fileName,
                    FullPath = fullPath,
                };
            }
            catch { return null; }
        }

        // Binary-search-friendly lookup. `sortedByBase` must be sorted by ImageBase ascending.
        public static string Lookup(IList<KernelModule> sortedByBase, ulong address)
        {
            if (sortedByBase == null || sortedByBase.Count == 0) return null;
            int lo = 0, hi = sortedByBase.Count - 1;
            int hit = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var m = sortedByBase[mid];
                if (address < m.ImageBase) hi = mid - 1;
                else if (address >= m.ImageBase + m.ImageSize) lo = mid + 1;
                else { hit = mid; break; }
            }
            return hit >= 0 ? sortedByBase[hit].FileName : null;
        }
    }
}
