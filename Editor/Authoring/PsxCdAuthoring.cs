/*
 * PlayStation 1 CD Image Authoring Tool - Pure C# / Unity
 *
 * Port of the pcsx-redux C++ authoring tool.
 * Produces a bootable PS1 CD-ROM image (.bin) with an embedded archive
 * of LZ4-compressed files, compatible with PSYQo's ArchiveManager.
 *
 * Dependencies:
 *   - Newtonsoft.Json  (Unity: com.unity.nuget.newtonsoft-json)
 *   - K4os.Compression.LZ4  (via NuGet or drop DLL in Plugins/)
 *
 * Usage:
 *   PsxCdAuthoring.CdAuthoring.Build("config.json", "output.bin");
 *
 * MIT License - Copyright (c) 2025 PCSX-Redux authors
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;
using Newtonsoft.Json.Linq;

namespace PsxCdAuthoring
{
    /// <summary>
    /// MSF (Minutes, Seconds, Frames) time format used in CD-ROM addressing.
    /// </summary>
    public struct MSF
    {
        public byte M, S, F;

        public MSF(byte m, byte s, byte f) { M = m; S = s; F = f; }

        public MSF(uint lba)
        {
            M = (byte)(lba / 75 / 60);
            lba -= (uint)(M * 75 * 60);
            S = (byte)(lba / 75);
            lba -= (uint)(S * 75);
            F = (byte)lba;
        }

        public uint ToLBA() => (uint)(M * 60 + S) * 75 + F;

        public void ToBCD(byte[] dst, int offset)
        {
            dst[offset + 0] = IntToBCD(M);
            dst[offset + 1] = IntToBCD(S);
            dst[offset + 2] = IntToBCD(F);
        }

        public void Increment()
        {
            F++;
            if (F >= 75) { F = 0; S++; }
            if (S >= 60) { S = 0; M++; }
        }

        public bool GreaterThan(MSF other)
        {
            if (M != other.M) return M > other.M;
            if (S != other.S) return S > other.S;
            return F > other.F;
        }

        private static byte IntToBCD(byte i) => (byte)((i / 10) * 16 + (i % 10));
    }

    /// <summary>
    /// DJB2 hash function matching the pcsx-redux / PSYQo implementation.
    /// </summary>
    public static class DjbHash
    {
        public static ulong Hash(string str)
        {
            ulong hash = 5381;
            for (int i = 0; i < str.Length; i++)
            {
                hash = ((hash << 5) + hash) ^ (byte)str[i];
            }
            return hash;
        }
    }

    /// <summary>
    /// EDC (Error Detection Code) and ECC (Error Correction Code) computation
    /// for CD-ROM Mode 2 sectors, per IEC 60908 / ECMA-130.
    /// </summary>
    public static class EdcEcc
    {
        // Yellow Book CRC32 table.
        // Polynomial: (x^16 + x^15 + x^2 + 1)(x^16 + x^2 + x + 1) = 0x8001801b
        private static readonly uint[] CrcTable;

        // Galois field GF(2^8) tables, primitive polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D)
        private static readonly byte[] GfMul2;
        private static readonly byte[] GfDiv3;

        static EdcEcc()
        {
            CrcTable = GenerateCrcTable(0x8001801b);
            var gfExp = new byte[512];
            var gfLog = new byte[256];
            GenerateGfTables(0x11d, gfExp, gfLog);
            GfMul2 = GenerateGfMul2Table(gfExp, gfLog);
            GfDiv3 = GenerateGfDiv3Table(GfMul2);
        }

        /// <summary>
        /// Compute EDC and ECC for a Mode 2 sector (2352 bytes).
        /// Modifies the sector buffer in-place.
        /// </summary>
        public static void ComputeEdcEcc(byte[] sector, int sectorOffset = 0)
        {
            int loc = sectorOffset + 12;
            byte mode = sector[loc + 3];
            if (mode != 2) return;

            int subheader = loc + 4;
            int form = (sector[subheader + 2] & 0x20) != 0 ? 2 : 1;
            int len = ((form == 2) ? 2324 : 2048) + 8;

            // Compute EDC (CRC32 over subheader + user data)
            uint edc = 0;
            for (int i = 0; i < len; i++)
            {
                edc = CrcTable[(edc ^ sector[subheader + i]) & 0xff] ^ (edc >> 8);
            }

            int edcPtr = subheader + len;
            sector[edcPtr + 0] = (byte)(edc & 0xff);
            sector[edcPtr + 1] = (byte)((edc >> 8) & 0xff);
            sector[edcPtr + 2] = (byte)((edc >> 16) & 0xff);
            sector[edcPtr + 3] = (byte)((edc >> 24) & 0xff);

            if (form == 2) return;

            // ECC computation for Mode 2 Form 1
            // Location field must be zeroed during ECC computation
            byte loc0 = sector[loc + 0], loc1 = sector[loc + 1], loc2 = sector[loc + 2], loc3 = sector[loc + 3];
            sector[loc + 0] = 0;
            sector[loc + 1] = 0;
            sector[loc + 2] = 0;
            sector[loc + 3] = 0;

            // P channel: 86 lines, 24 data bytes each, stride 86
            for (int i = 0; i < 86; i++)
            {
                ushort ecc = 0;
                for (int j = 0; j < 24; j++)
                {
                    byte coeff = sector[loc + 86 * j + i];
                    ecc = (ushort)(GfMul2[(ecc & 0xff) ^ coeff] | ((ecc & 0xff00) ^ ((ushort)coeff << 8)));
                }
                byte eccHigh = (byte)(ecc >> 8);
                byte eccLow = GfDiv3[GfMul2[ecc & 0xff] ^ eccHigh];
                eccHigh ^= eccLow;

                sector[loc + 24 * 86 + i] = eccLow;
                sector[loc + 25 * 86 + i] = eccHigh;
            }

            // Q channel: 52 lines, 43 data bytes each, complex stride
            for (int i = 0; i < 52; i++)
            {
                ushort ecc = 0;
                for (int j = 0; j < 43; j++)
                {
                    int l = ((44 * j + 43 * (i / 2)) % 1118) * 2 + (i & 1);
                    byte coeff = sector[loc + l];
                    ecc = (ushort)(GfMul2[(ecc & 0xff) ^ coeff] | ((ecc & 0xff00) ^ ((ushort)coeff << 8)));
                }
                byte eccHigh = (byte)(ecc >> 8);
                byte eccLow = GfDiv3[GfMul2[ecc & 0xff] ^ eccHigh];
                eccHigh ^= eccLow;

                sector[loc + 43 * 26 * 2 + i] = eccLow;
                sector[loc + 44 * 26 * 2 + i] = eccHigh;
            }

            // Restore location field
            sector[loc + 0] = loc0;
            sector[loc + 1] = loc1;
            sector[loc + 2] = loc2;
            sector[loc + 3] = loc3;
        }

        private static uint Reverse32(uint v)
        {
            v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
            v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
            v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
            v = ((v >> 8) & 0x00FF00FF) | ((v & 0x00FF00FF) << 8);
            return (v >> 16) | (v << 16);
        }

        private static uint[] GenerateCrcTable(uint poly)
        {
            uint rPoly = Reverse32(poly);
            var table = new uint[256];
            for (uint d = 0; d < 256; d++)
            {
                uint r = d;
                for (int i = 0; i < 8; i++)
                {
                    uint flip = (r & 1) != 0 ? rPoly : 0;
                    r >>= 1;
                    r ^= flip;
                }
                table[d] = r;
            }
            return table;
        }

        private static void GenerateGfTables(uint prim, byte[] exp, byte[] log)
        {
            uint x = 1;
            for (int i = 0; i < 512; i++)
            {
                exp[i] = (byte)x;
                x <<= 1;
                x ^= (x >= 256) ? prim : 0;
            }
            for (int i = 0; i < 256; i++) log[i] = 0;
            for (int i = 0; i < 255; i++) log[exp[i]] = (byte)i;
        }

        private static byte[] GenerateGfMul2Table(byte[] exp, byte[] log)
        {
            var table = new byte[256];
            table[0] = 0;
            for (int i = 1; i < 256; i++)
                table[i] = exp[log[i] + log[2]];
            return table;
        }

        private static byte[] GenerateGfDiv3Table(byte[] mul2)
        {
            var table = new byte[256];
            for (int i = 0; i < 256; i++)
                table[mul2[i] ^ (byte)i] = (byte)i;
            return table;
        }
    }

    /// <summary>
    /// ISO 9660 CD image builder. Writes 2352-byte raw sectors to a .bin file.
    /// </summary>
    public class Iso9660Builder : IDisposable
    {
        public const int FrameSizeRaw = 2352;

        private static readonly byte[] SyncPattern = {
            0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
        };

        private readonly FileStream _out;
        private MSF _location = new MSF(0, 2, 0);

        public Iso9660Builder(string outputPath)
        {
            _out = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        }

        public MSF CurrentLocation => _location;

        /// <summary>
        /// Write the 16-sector license area (sectors 0-15).
        /// </summary>
        public void WriteLicense(string licensePath = null)
        {
            if (!string.IsNullOrEmpty(licensePath) && File.Exists(licensePath))
            {
                byte[] licenseData = new byte[FrameSizeRaw * 16];
                using (var fs = File.OpenRead(licensePath))
                    fs.Read(licenseData, 0, licenseData.Length);

                if (licenseData[0x2492] == (byte)'L')
                {
                    // SDK license file in 2336 bytes per sector format
                    for (int i = 0; i < 16; i++)
                    {
                        byte[] sectorData = new byte[2048];
                        Array.Copy(licenseData, 2336 * i + 8, sectorData, 0, Math.Min(2048, 2336 - 8));
                        WriteSectorAt(sectorData, new MSF(0, 2, (byte)i), SectorMode.M2Form1);
                    }
                    return;
                }
                else if (licenseData[0x24e2] == (byte)'L')
                {
                    // Raw ISO format license
                    for (int i = 0; i < 16; i++)
                    {
                        byte[] rawSector = new byte[FrameSizeRaw];
                        Array.Copy(licenseData, FrameSizeRaw * i, rawSector, 0, FrameSizeRaw);
                        WriteSectorAt(rawSector, new MSF(0, 2, (byte)i), SectorMode.Raw);
                    }
                    return;
                }
            }

            // No license - write empty sectors
            byte[] empty = new byte[2048];
            for (int i = 0; i < 16; i++)
                WriteSectorAt(empty, new MSF(0, 2, (byte)i), SectorMode.M2Form1);
        }

        /// <summary>
        /// Write a sector at the specified MSF location.
        /// </summary>
        public void WriteSectorAt(byte[] sectorData, MSF msf, SectorMode mode)
        {
            uint lba = msf.ToLBA() - 150;
            long offset = (long)lba * FrameSizeRaw;

            switch (mode)
            {
                case SectorMode.Raw:
                    WriteAt(sectorData, 0, FrameSizeRaw, offset);
                    break;

                case SectorMode.M2Form1:
                {
                    byte[] sector = new byte[FrameSizeRaw];
                    Array.Copy(SyncPattern, 0, sector, 0, 12);
                    msf.ToBCD(sector, 12);
                    sector[15] = 2; // Mode 2
                    // Subheader (Form 1)
                    sector[16] = sector[20] = 0;
                    sector[17] = sector[21] = 0;
                    sector[18] = sector[22] = 8;
                    sector[19] = sector[23] = 0;
                    Array.Copy(sectorData, 0, sector, 24, Math.Min(sectorData.Length, 2048));
                    EdcEcc.ComputeEdcEcc(sector);
                    WriteAt(sector, 0, FrameSizeRaw, offset);
                    break;
                }

                case SectorMode.M2Form2:
                {
                    byte[] sector = new byte[FrameSizeRaw];
                    Array.Copy(SyncPattern, 0, sector, 0, 12);
                    msf.ToBCD(sector, 12);
                    sector[15] = 2;
                    sector[16] = sector[20] = 0;
                    sector[17] = sector[21] = 0;
                    sector[18] = sector[22] = 0x28; // Form 2 flag
                    sector[19] = sector[23] = 0;
                    Array.Copy(sectorData, 0, sector, 24, Math.Min(sectorData.Length, 2324));
                    EdcEcc.ComputeEdcEcc(sector);
                    WriteAt(sector, 0, FrameSizeRaw, offset);
                    break;
                }
            }

            msf.Increment();
            if (msf.GreaterThan(_location)) _location = msf;
        }

        private void WriteAt(byte[] data, int dataOffset, int length, long fileOffset)
        {
            _out.Seek(fileOffset, SeekOrigin.Begin);
            _out.Write(data, dataOffset, length);
        }

        public void Dispose()
        {
            _out?.Dispose();
        }
    }

    public enum SectorMode
    {
        Raw,
        M2Form1,
        M2Form2,
    }

    /// <summary>
    /// Archive index entry - 16 bytes, matching the PSYQo ArchiveManager format.
    /// Layout: 8 bytes hash + 8 bytes bitfield.
    /// Bitfield: [20:0] DecompSize, [31:21] Padding, [50:32] SectorOffset,
    ///           [60:51] CompressedSize, [63:61] Method
    /// </summary>
    public struct IndexEntry
    {
        public const int SizeBytes = 16;

        public enum CompressionMethod : uint
        {
            None = 0,
            UclNrv2e = 1,
            Lz4 = 2,
        }

        public ulong Hash;
        public ulong Entry;

        public uint DecompSize
        {
            get => (uint)(Entry & 0x1FFFFF);
            set => Entry = (Entry & ~0x1FFFFFul) | (value & 0x1FFFFF);
        }

        public uint Padding
        {
            get => (uint)((Entry >> 21) & 0x7FF);
            set => Entry = (Entry & ~(0x7FFul << 21)) | (((ulong)value & 0x7FF) << 21);
        }

        public uint SectorOffset
        {
            get => (uint)((Entry >> 32) & 0x7FFFF);
            set => Entry = (Entry & ~(0x7FFFFul << 32)) | (((ulong)value & 0x7FFFF) << 32);
        }

        public uint CompressedSize
        {
            get => (uint)((Entry >> 51) & 0x3FF);
            set => Entry = (Entry & ~(0x3FFul << 51)) | (((ulong)value & 0x3FF) << 51);
        }

        public CompressionMethod Method
        {
            get => (CompressionMethod)((Entry >> 61) & 0x7);
            set => Entry = (Entry & ~(0x7ul << 61)) | (((ulong)value & 0x7) << 61);
        }

        public void WriteTo(byte[] buffer, int offset)
        {
            WriteUInt64LE(buffer, offset, Hash);
            WriteUInt64LE(buffer, offset + 8, Entry);
        }

        private static void WriteUInt64LE(byte[] buf, int off, ulong v)
        {
            buf[off + 0] = (byte)(v);
            buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
            buf[off + 4] = (byte)(v >> 32);
            buf[off + 5] = (byte)(v >> 40);
            buf[off + 6] = (byte)(v >> 48);
            buf[off + 7] = (byte)(v >> 56);
        }
    }

    /// <summary>
    /// Main CD authoring tool. Reads a JSON configuration and produces a bootable
    /// PS1 CD image with an embedded archive of LZ4-compressed files.
    /// </summary>
    public static class CdAuthoring
    {
        private const int MaxSectorCount = (99 * 60 + 59) * 75 + 74 - 150;

        /// <summary>
        /// Build a CD image from a JSON configuration file.
        /// </summary>
        /// <param name="jsonPath">Path to the input JSON configuration.</param>
        /// <param name="outputPath">Path for the output .bin file.</param>
        /// <param name="licensePath">Optional path to a PS1 license file.</param>
        /// <param name="quiet">Suppress non-error output.</param>
        public static void Build(string jsonPath, string outputPath, string licensePath = null, bool quiet = false)
        {
            string jsonText = File.ReadAllText(jsonPath);
            var config = JObject.Parse(jsonText);

            string basePath = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
            Build(config, basePath, outputPath, licensePath, quiet);
        }

        /// <summary>
        /// Build a CD image from a parsed JSON configuration.
        /// </summary>
        /// <param name="config">Parsed JSON configuration.</param>
        /// <param name="basePath">Base directory for resolving file paths.</param>
        /// <param name="outputPath">Path for the output .bin file.</param>
        /// <param name="licensePath">Optional path to a PS1 license file.</param>
        /// <param name="quiet">Suppress non-error output.</param>
        public static void Build(JObject config, string basePath, string outputPath,
                                 string licensePath = null, bool quiet = false)
        {
            // Validate configuration
            if (config["executable"] == null || config["executable"].Type != JTokenType.String)
                throw new ArgumentException("JSON must contain a string 'executable' field.");
            if (config["files"] == null || config["files"].Type != JTokenType.Array)
                throw new ArgumentException("JSON must contain an array 'files' field.");

            string executablePath = config["executable"].Value<string>();
            var files = (JArray)config["files"];
            int filesCount = files.Count;

            if (filesCount > MaxSectorCount)
                throw new ArgumentException($"Too many files ({filesCount}), max is {MaxSectorCount}.");

            var pvd = config["pvd"] as JObject ?? new JObject();

            // Read the executable file
            string fullExePath = Path.Combine(basePath, executablePath);
            byte[] exeData = File.ReadAllBytes(fullExePath);

            // Pad executable to 2048-byte boundary
            int paddedExeSize = (exeData.Length + 2047) & ~2047;
            if (paddedExeSize != exeData.Length)
            {
                byte[] padded = new byte[paddedExeSize];
                Array.Copy(exeData, padded, exeData.Length);
                exeData = padded;
            }
            int executableSectorsCount = paddedExeSize / 2048;

            // Calculate index size
            int indexSectorsCount = ((filesCount + 1) * IndexEntry.SizeBytes + 2047) / 2048;

            if (!quiet)
            {
                Log($"Index size: {indexSectorsCount * 2048}");
                Log($"Executable size: {paddedExeSize}");
                Log($"Executable location: sector {23 + indexSectorsCount}");
            }

            using (var builder = new Iso9660Builder(outputPath))
            {
                // Write license area (sectors 0-15)
                builder.WriteLicense(licensePath);

                // Write executable sectors
                uint currentSector = (uint)(23 + indexSectorsCount);
                for (int i = 0; i < executableSectorsCount; i++)
                {
                    byte[] sectorData = new byte[2048];
                    Array.Copy(exeData, i * 2048, sectorData, 0, 2048);
                    builder.WriteSectorAt(sectorData, new MSF(150 + currentSector),
                                          SectorMode.M2Form1);
                    currentSector++;
                }

                // Process and compress each file
                var indexEntries = new IndexEntry[filesCount];
                var fileSectorData = new List<byte[][]>(filesCount);

                for (int fi = 0; fi < filesCount; fi++)
                {
                    var fileInfo = files[fi] as JObject;
                    if (fileInfo == null || fileInfo["path"] == null)
                        throw new ArgumentException($"File entry {fi} is invalid.");

                    string filePath = fileInfo["path"].Value<string>();
                    string fullPath = Path.Combine(basePath, filePath);
                    byte[] fileData = File.ReadAllBytes(fullPath);

                    if (fileData.Length >= 2 * 1024 * 1024)
                        throw new ArgumentException($"File too large (>= 2MB): {filePath}");

                    int originalSize = fileData.Length;
                    int originalSectors = (originalSize + 2047) / 2048;

                    // Pad to sector boundary
                    byte[] paddedData = new byte[originalSectors * 2048];
                    Array.Copy(fileData, paddedData, fileData.Length);

                    // Try LZ4 compression
                    byte[] compBuffer = new byte[(int)(fileData.Length * 1.1) + 2048];
                    int compressedSize = LZ4Codec.Encode(
                        fileData, 0, originalSize,
                        compBuffer, 0, compBuffer.Length,
                        LZ4Level.L12_MAX);

                    int compressedSectors = compressedSize > 0 ? (compressedSize + 2047) / 2048 : int.MaxValue;

                    ref IndexEntry entry = ref indexEntries[fi];

                    // Determine hash
                    string hashName = fileInfo["name"]?.Value<string>() ?? filePath;
                    entry.Hash = DjbHash.Hash(hashName);
                    entry.DecompSize = (uint)originalSize;

                    byte[][] sectors;

                    if (compressedSize > 0 && compressedSectors < originalSectors)
                    {
                        // Use LZ4 compression
                        entry.CompressedSize = (uint)compressedSectors;
                        entry.Method = IndexEntry.CompressionMethod.Lz4;

                        int padding = compressedSize % 2048;
                        if (padding > 0) padding = 2048 - padding;
                        entry.Padding = (uint)padding;

                        // Build sector data: padding bytes first, then compressed data
                        byte[] sectorBlob = new byte[compressedSectors * 2048];
                        Array.Copy(compBuffer, 0, sectorBlob, padding, compressedSize);

                        sectors = BuildRawSectors(sectorBlob, compressedSectors);
                    }
                    else
                    {
                        // Store uncompressed
                        entry.CompressedSize = (uint)originalSectors;
                        entry.Method = IndexEntry.CompressionMethod.None;
                        entry.Padding = 0;

                        sectors = BuildRawSectors(paddedData, originalSectors);
                    }

                    fileSectorData.Add(sectors);

                    if (!quiet)
                    {
                        Log($"Processed file: {filePath}");
                        Log($"  Original size: {originalSize}");
                        Log($"  Compressed size: {entry.CompressedSize * 2048}");
                        Log($"  Compression method: {(uint)entry.Method}");
                        Log($"  Sector offset: {currentSector}");
                    }

                    entry.SectorOffset = currentSector;
                    int sectorCount = (int)entry.CompressedSize;

                    // Write file sectors
                    for (int s = 0; s < sectorCount; s++)
                    {
                        SetSectorLBA(sectors[s], currentSector);
                        builder.WriteSectorAt(sectors[s], new MSF(150 + currentSector), SectorMode.Raw);
                        currentSector++;
                    }
                }

                if (!quiet)
                    Log($"Processed {filesCount} files.");

                // Write 9000 empty padding sectors
                byte[] emptySector = new byte[2048];
                for (int i = 0; i < 9000; i++)
                {
                    builder.WriteSectorAt(emptySector, new MSF(150 + currentSector), SectorMode.M2Form1);
                    currentSector++;
                }

                uint totalSectorCount = currentSector;

                // Build and write the archive index at sector 23
                byte[] indexBuffer = new byte[indexSectorsCount * 2048];

                // Header: "PSX-ARC1" magic + file count + total sectors
                indexBuffer[0] = (byte)'P';
                indexBuffer[1] = (byte)'S';
                indexBuffer[2] = (byte)'X';
                indexBuffer[3] = (byte)'-';
                indexBuffer[4] = (byte)'A';
                indexBuffer[5] = (byte)'R';
                indexBuffer[6] = (byte)'C';
                indexBuffer[7] = (byte)'1';
                WriteUInt32LE(indexBuffer, 8, (uint)filesCount);
                WriteUInt32LE(indexBuffer, 12, totalSectorCount);

                // Sort entries by hash (binary search compatibility)
                Array.Sort(indexEntries, (a, b) => a.Hash.CompareTo(b.Hash));

                // Write entries (starting at offset 16 = after header, which is one IndexEntry)
                for (int i = 0; i < filesCount; i++)
                {
                    indexEntries[i].WriteTo(indexBuffer, (i + 1) * IndexEntry.SizeBytes);
                }

                // Write index sectors
                for (int i = 0; i < indexSectorsCount; i++)
                {
                    byte[] sectorData = new byte[2048];
                    Array.Copy(indexBuffer, i * 2048, sectorData, 0, 2048);
                    builder.WriteSectorAt(sectorData, new MSF(0, 2, (byte)(23 + i)), SectorMode.M2Form1);
                }

                // Write PVD (Primary Volume Descriptor) at sector 16
                WritePVD(builder, pvd, totalSectorCount);

                // Write volume descriptor set terminator at sector 17
                WritePVDTerminator(builder);

                // Write path tables at sectors 18-21
                WritePathTables(builder);

                // Write root directory at sector 22
                WriteRootDirectory(builder, indexSectorsCount, executableSectorsCount);
            }
        }

        /// <summary>
        /// Build pre-framed raw 2352-byte sectors from user data.
        /// MSF is left at zero and must be filled in before writing.
        /// </summary>
        private static byte[][] BuildRawSectors(byte[] data, int sectorCount)
        {
            var sectors = new byte[sectorCount][];
            for (int i = 0; i < sectorCount; i++)
            {
                byte[] sector = new byte[Iso9660Builder.FrameSizeRaw];

                // Sync pattern
                sector[0] = 0x00;
                for (int j = 1; j <= 10; j++) sector[j] = 0xFF;
                sector[11] = 0x00;

                // MSF (zeroed, filled later) + Mode 2
                sector[15] = 2;

                // Subheader (Mode 2 Form 1)
                sector[18] = sector[22] = 8;

                // User data
                Array.Copy(data, i * 2048, sector, 24, 2048);

                // Compute EDC/ECC
                EdcEcc.ComputeEdcEcc(sector);

                sectors[i] = sector;
            }
            return sectors;
        }

        /// <summary>
        /// Set the MSF timestamp in a pre-built raw sector.
        /// </summary>
        private static void SetSectorLBA(byte[] sector, uint lba)
        {
            var msf = new MSF(lba + 150);
            msf.ToBCD(sector, 12);
        }

        private static void WritePVD(Iso9660Builder builder, JObject pvd, uint totalSectorCount)
        {
            byte[] sector = new byte[2048];

            sector[0] = 1; // Type code
            // "CD001"
            sector[1] = (byte)'C'; sector[2] = (byte)'D';
            sector[3] = (byte)'0'; sector[4] = (byte)'0'; sector[5] = (byte)'1';
            sector[6] = 1; // Version

            // System Identifier (offset 8, 32 bytes, padded with spaces)
            string systemId = pvd["system_id"]?.Value<string>() ?? "PLAYSTATION";
            WriteStringPadded(sector, 8, 32, systemId, (byte)' ');

            // Volume Identifier (offset 40, 32 bytes)
            string volumeId = pvd["volume_id"]?.Value<string>() ?? "";
            WriteStringPadded(sector, 40, 32, volumeId, (byte)' ');

            // Volume Space Size (offset 80, both-endian uint32)
            WriteUInt32LE(sector, 80, totalSectorCount);
            WriteUInt32BE(sector, 84, totalSectorCount);

            // Volume Set Size (offset 120, both-endian uint16)
            WriteUInt16LE(sector, 120, 1);
            WriteUInt16BE(sector, 122, 1);

            // Volume Sequence Number (offset 124)
            WriteUInt16LE(sector, 124, 1);
            WriteUInt16BE(sector, 126, 1);

            // Logical Block Size (offset 128)
            WriteUInt16LE(sector, 128, 2048);
            WriteUInt16BE(sector, 130, 2048);

            // Path Table Size (offset 132)
            WriteUInt32LE(sector, 132, 10);
            WriteUInt32BE(sector, 136, 10);

            // Path Table Locations (offset 140)
            WriteUInt32LE(sector, 140, 18); // L Path Table
            WriteUInt32LE(sector, 144, 19); // L Path Table Optional
            WriteUInt32BE(sector, 148, 20); // M Path Table
            WriteUInt32BE(sector, 152, 21); // M Path Table Optional

            // Root Directory Entry (offset 156, 34 bytes)
            WriteRootDirEntry(sector, 156);

            // Volume Set Identifier (offset 190, 128 bytes)
            string volSetId = pvd["volume_set_id"]?.Value<string>() ?? "";
            WriteStringPadded(sector, 190, 128, volSetId, (byte)' ');

            // Publisher Identifier (offset 318, 128 bytes)
            string publisher = pvd["publisher"]?.Value<string>() ?? "";
            WriteStringPadded(sector, 318, 128, publisher, (byte)' ');

            // Data Preparer Identifier (offset 446, 128 bytes)
            string preparer = pvd["preparer"]?.Value<string>() ?? "";
            WriteStringPadded(sector, 446, 128, preparer, (byte)' ');

            // Application Identifier (offset 574, 128 bytes)
            string appId = pvd["application_id"]?.Value<string>() ?? "";
            WriteStringPadded(sector, 574, 128, appId, (byte)' ');

            // Copyright File Identifier (offset 702, 37 bytes)
            string copyright = pvd["copyright"]?.Value<string>() ?? "";
            WriteStringPadded(sector, 702, 37, copyright, (byte)' ');

            // Abstract File Identifier (offset 739, 37 bytes)
            string abstractId = pvd["abstract"]?.Value<string>() ?? "";
            WriteStringPadded(sector, 739, 37, abstractId, (byte)' ');

            // Bibliographic File Identifier (offset 776, 37 bytes)
            string biblio = pvd["bibliographic"]?.Value<string>() ?? "";
            WriteStringPadded(sector, 776, 37, biblio, (byte)' ');

            // File Structure Version (offset 881)
            sector[881] = 1;

            builder.WriteSectorAt(sector, new MSF(0, 2, 16), SectorMode.M2Form1);
        }

        private static void WriteRootDirEntry(byte[] sector, int offset)
        {
            sector[offset + 0] = 34;  // Length
            sector[offset + 1] = 0;   // Extended attribute length
            WriteUInt32LE(sector, offset + 2, 22);  // LBA
            WriteUInt32BE(sector, offset + 6, 22);  // LBA (BE)
            WriteUInt32LE(sector, offset + 10, 2048); // Size
            WriteUInt32BE(sector, offset + 14, 2048); // Size (BE)
            // Date (7 bytes, all zero)
            sector[offset + 25] = 2;  // Flags: directory
            // Volume sequence number
            WriteUInt16LE(sector, offset + 28, 1);
            WriteUInt16BE(sector, offset + 30, 1);
            sector[offset + 32] = 1;  // Filename length
            sector[offset + 33] = 0;  // Filename (root = 0x00)
        }

        private static void WritePVDTerminator(Iso9660Builder builder)
        {
            byte[] sector = new byte[2048];
            sector[0] = 0xFF; // Terminator type
            sector[1] = (byte)'C'; sector[2] = (byte)'D';
            sector[3] = (byte)'0'; sector[4] = (byte)'0'; sector[5] = (byte)'1';
            builder.WriteSectorAt(sector, new MSF(0, 2, 17), SectorMode.M2Form1);
        }

        private static void WritePathTables(Iso9660Builder builder)
        {
            // Little-endian path table
            byte[] leTable = new byte[2048];
            leTable[0] = 1;     // Name length
            leTable[2] = 22;    // Extent location (LE)
            leTable[6] = 1;     // Parent directory number (LE)

            builder.WriteSectorAt(leTable, new MSF(0, 2, 18), SectorMode.M2Form1);
            builder.WriteSectorAt(leTable, new MSF(0, 2, 19), SectorMode.M2Form1);

            // Big-endian path table
            byte[] beTable = new byte[2048];
            beTable[0] = 1;
            beTable[5] = 22;    // Extent location (BE)
            beTable[7] = 1;     // Parent directory number (BE)

            builder.WriteSectorAt(beTable, new MSF(0, 2, 20), SectorMode.M2Form1);
            builder.WriteSectorAt(beTable, new MSF(0, 2, 21), SectorMode.M2Form1);
        }

        private static void WriteRootDirectory(Iso9660Builder builder,
                                               int indexSectorsCount, int executableSectorsCount)
        {
            // This matches the C++ tool's hardcoded root directory exactly.
            // Contains: "." entry, ".." entry, and "PSX.EXE;1" entry.
            byte[] sector = new byte[2048];

            // "." entry (self, 34 bytes)
            sector[0] = 0x22; // Length = 34
            WriteUInt32LE(sector, 2, 22);    // LBA
            WriteUInt32BE(sector, 6, 22);    // LBA (BE)
            WriteUInt32LE(sector, 10, 2048); // Size
            WriteUInt32BE(sector, 14, 2048); // Size (BE)
            sector[25] = 2;   // Flags: directory
            WriteUInt16LE(sector, 28, 1);    // Vol seq
            WriteUInt16BE(sector, 30, 1);
            sector[32] = 1;   // Name length
            sector[33] = 0;   // Name: 0x00 (self)

            // ".." entry (parent, 34 bytes, starts at offset 34)
            sector[34] = 0x22;
            WriteUInt32LE(sector, 36, 22);
            WriteUInt32BE(sector, 40, 22);
            WriteUInt32LE(sector, 44, 2048);
            WriteUInt32BE(sector, 48, 2048);
            sector[59] = 2;
            WriteUInt16LE(sector, 62, 1);
            WriteUInt16BE(sector, 64, 1);
            sector[66] = 1;
            sector[67] = 1;   // Name: 0x01 (parent)

            // PSX.EXE;1 entry (starts at offset 68)
            int off = 68;
            sector[off + 0] = 0x2A; // Length = 42

            // LBA of the executable = sector 23 + indexSectors
            uint exeLBA = (uint)(23 + indexSectorsCount);
            WriteUInt32LE(sector, off + 2, exeLBA);
            WriteUInt32BE(sector, off + 6, exeLBA);

            // Size of the executable
            uint exeSize = (uint)(executableSectorsCount * 2048);
            WriteUInt32LE(sector, off + 10, exeSize);
            WriteUInt32BE(sector, off + 14, exeSize);

            // Date (7 bytes, all zero)
            // Flags = 0 (regular file)

            WriteUInt16LE(sector, off + 28, 1); // Vol seq
            WriteUInt16BE(sector, off + 30, 1);

            // Filename: "PSX.EXE;1" (9 bytes)
            sector[off + 32] = 9; // Name length
            byte[] name = Encoding.ASCII.GetBytes("PSX.EXE;1");
            Array.Copy(name, 0, sector, off + 33, 9);

            builder.WriteSectorAt(sector, new MSF(0, 2, 22), SectorMode.M2Form1);
        }

        // Endian helpers
        private static void WriteUInt32LE(byte[] buf, int off, uint v)
        {
            buf[off + 0] = (byte)(v);
            buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
        }

        private static void WriteUInt32BE(byte[] buf, int off, uint v)
        {
            buf[off + 0] = (byte)(v >> 24);
            buf[off + 1] = (byte)(v >> 16);
            buf[off + 2] = (byte)(v >> 8);
            buf[off + 3] = (byte)(v);
        }

        private static void WriteUInt16LE(byte[] buf, int off, ushort v)
        {
            buf[off + 0] = (byte)(v);
            buf[off + 1] = (byte)(v >> 8);
        }

        private static void WriteUInt16BE(byte[] buf, int off, ushort v)
        {
            buf[off + 0] = (byte)(v >> 8);
            buf[off + 1] = (byte)(v);
        }

        private static void WriteStringPadded(byte[] buf, int off, int len, string str, byte pad)
        {
            int copyLen = Math.Min(str.Length, len);
            for (int i = 0; i < copyLen; i++)
                buf[off + i] = (byte)str[i];
            for (int i = copyLen; i < len; i++)
                buf[off + i] = pad;
        }

        private static void Log(string message)
        {
#if UNITY_EDITOR
            UnityEngine.Debug.Log(message);
#else
            Console.WriteLine(message);
#endif
        }
    }
}
