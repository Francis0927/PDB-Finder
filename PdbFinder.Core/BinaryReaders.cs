using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PdbFinder.Core
{
    public sealed class PdbIdentity : IEquatable<PdbIdentity>
    {
        public PdbIdentity(Guid guid, uint age) { Guid = guid; Age = age; }
        public Guid Guid { get; }
        public uint Age { get; }
        public bool Equals(PdbIdentity other) => other != null && Guid == other.Guid && Age == other.Age;
        public override bool Equals(object obj) => Equals(obj as PdbIdentity);
        public override int GetHashCode() => Guid.GetHashCode() ^ Age.GetHashCode();
        public override string ToString() => Guid.ToString("N").ToUpperInvariant() + Age.ToString("X");
    }

    public sealed class DumpModule
    {
        public string Name { get; internal set; }
        public uint TimeDateStamp { get; internal set; }
        public uint SizeOfImage { get; internal set; }
        public uint CheckSum { get; internal set; }
        public string ExpectedPdbName { get; internal set; }
        public PdbIdentity Pdb { get; internal set; }
    }

    public sealed class PeIdentity
    {
        public uint TimeDateStamp { get; internal set; }
        public uint SizeOfImage { get; internal set; }
        public uint CheckSum { get; internal set; }
        public PdbIdentity Pdb { get; internal set; }
    }

    internal static class BinaryRead
    {
        public static byte[] At(Stream stream, long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > stream.Length - count) throw new InvalidDataException("File offset is outside the file.");
            var result = new byte[count];
            stream.Position = offset;
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(result, read, count - read);
                if (n == 0) throw new EndOfStreamException();
                read += n;
            }
            return result;
        }
        public static uint U32(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
        public static ushort U16(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
        public static string CStr(byte[] data, int offset)
        {
            var end = offset;
            while (end < data.Length && data[end] != 0) end++;
            return Encoding.UTF8.GetString(data, offset, end - offset);
        }
        public static PdbIdentity Rsds(byte[] data)
        {
            if (data.Length < 25 || Encoding.ASCII.GetString(data, 0, 4) != "RSDS") return null;
            var guid = new byte[16];
            Buffer.BlockCopy(data, 4, guid, 0, 16);
            return new PdbIdentity(new Guid(guid), U32(data, 20));
        }
    }

    public static class MinidumpReader
    {
        public static IReadOnlyList<DumpModule> ReadModules(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var header = BinaryRead.At(file, 0, 32);
                if (BinaryRead.U32(header, 0) != 0x504d444d) throw new InvalidDataException("Not a Windows minidump.");
                var streamCount = BinaryRead.U32(header, 8);
                var directoryRva = BinaryRead.U32(header, 12);
                if (streamCount > 4096) throw new InvalidDataException("Too many minidump streams.");
                var directory = BinaryRead.At(file, directoryRva, checked((int)streamCount * 12));
                for (var i = 0; i < streamCount; i++)
                {
                    var o = i * 12;
                    if (BinaryRead.U32(directory, o) != 4) continue;
                    var size = BinaryRead.U32(directory, o + 4);
                    var rva = BinaryRead.U32(directory, o + 8);
                    if (size < 4) throw new InvalidDataException("Invalid module stream.");
                    var count = BinaryRead.U32(BinaryRead.At(file, rva, 4), 0);
                    if (count > (size - 4) / 108) throw new InvalidDataException("Invalid module count.");
                    var result = new List<DumpModule>();
                    for (var m = 0; m < count; m++)
                    {
                        var record = BinaryRead.At(file, (long)rva + 4 + m * 108L, 108);
                        var nameRva = BinaryRead.U32(record, 20);
                        var nameLength = BinaryRead.U32(BinaryRead.At(file, nameRva, 4), 0);
                        if (nameLength > 32768 || (nameLength & 1) != 0) throw new InvalidDataException("Invalid module name.");
                        var name = Encoding.Unicode.GetString(BinaryRead.At(file, nameRva + 4L, (int)nameLength));
                        var cvSize = BinaryRead.U32(record, 76);
                        var cvRva = BinaryRead.U32(record, 80);
                        byte[] cv = cvSize >= 25 && cvSize < 32768 ? BinaryRead.At(file, cvRva, (int)cvSize) : null;
                        result.Add(new DumpModule
                        {
                            Name = name,
                            SizeOfImage = BinaryRead.U32(record, 8),
                            CheckSum = BinaryRead.U32(record, 12),
                            TimeDateStamp = BinaryRead.U32(record, 16),
                            Pdb = cv == null ? null : BinaryRead.Rsds(cv),
                            ExpectedPdbName = cv == null || BinaryRead.Rsds(cv) == null ? null : Path.GetFileName(BinaryRead.CStr(cv, 24))
                        });
                    }
                    return result;
                }
                throw new InvalidDataException("ModuleListStream is missing.");
            }
        }

        public static DumpModule MainExecutable(string path) => ReadModules(path).FirstOrDefault(m => m.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        // MiniDumpWithFullMemory captures the whole image of every module, so the
        // debugger needs no on-disk binary at all -- only the PDB.
        public static bool HasFullMemory(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var header = BinaryRead.At(file, 0, 32);
                if (BinaryRead.U32(header, 0) != 0x504d444d) throw new InvalidDataException("Not a Windows minidump.");
                return (BinaryRead.U32(header, 24) & 0x2) != 0;
            }
        }
    }

    public static class PdbReader
    {
        public static PdbIdentity Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess))
            {
                var header = BinaryRead.At(file, 0, 56);
                if (!Encoding.ASCII.GetString(header, 0, 24).StartsWith("Microsoft C/C++ MSF 7.00", StringComparison.Ordinal))
                    throw new InvalidDataException("Unsupported PDB format.");
                var blockSize = BinaryRead.U32(header, 32);
                var directorySize = BinaryRead.U32(header, 44);
                var blockMap = BinaryRead.U32(header, 52);
                if (blockSize < 512 || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0 || directorySize > 64 * 1024 * 1024)
                    throw new InvalidDataException("Invalid PDB superblock.");
                var directoryBlockCount = (directorySize + blockSize - 1) / blockSize;
                var map = BinaryRead.At(file, (long)blockMap * blockSize, checked((int)directoryBlockCount * 4));
                var directory = ReadBlocks(file, blockSize, directorySize, map, 0);
                var streamCount = BinaryRead.U32(directory, 0);
                if (streamCount < 2 || streamCount > 100000) throw new InvalidDataException("Invalid PDB stream count.");
                var sizesEnd = checked(4 + (int)streamCount * 4);
                if (sizesEnd > directory.Length) throw new InvalidDataException("Truncated PDB directory.");
                var blockListOffset = sizesEnd;
                for (var streamIndex = 0; streamIndex < streamCount; streamIndex++)
                {
                    var size = BinaryRead.U32(directory, 4 + streamIndex * 4);
                    if (size == uint.MaxValue) continue;
                    var blocks = (size + blockSize - 1) / blockSize;
                    var bytes = checked((int)blocks * 4);
                    if (blockListOffset > directory.Length - bytes) throw new InvalidDataException("Truncated PDB block list.");
                    if (streamIndex == 1)
                    {
                        if (size < 28 || size > 1024 * 1024) throw new InvalidDataException("Invalid PDB info stream.");
                        var info = ReadBlocks(file, blockSize, size, directory, blockListOffset);
                        var guid = new byte[16];
                        Buffer.BlockCopy(info, 12, guid, 0, 16);
                        return new PdbIdentity(new Guid(guid), BinaryRead.U32(info, 8));
                    }
                    blockListOffset += bytes;
                }
                throw new InvalidDataException("PDB info stream is missing.");
            }
        }

        private static byte[] ReadBlocks(Stream file, uint blockSize, uint byteCount, byte[] blockNumbers, int offset)
        {
            var result = new byte[checked((int)byteCount)];
            var copied = 0;
            var blockIndex = 0;
            while (copied < result.Length)
            {
                var block = BinaryRead.U32(blockNumbers, offset + blockIndex * 4);
                var take = Math.Min((int)blockSize, result.Length - copied);
                var bytes = BinaryRead.At(file, (long)block * blockSize, take);
                Buffer.BlockCopy(bytes, 0, result, copied, take);
                copied += take;
                blockIndex++;
            }
            return result;
        }
    }

    public static class PeReader
    {
        public static PeIdentity Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess))
            {
                var dos = BinaryRead.At(file, 0, 64);
                if (BinaryRead.U16(dos, 0) != 0x5a4d) throw new InvalidDataException("Not a PE image.");
                var peOffset = BinaryRead.U32(dos, 60);
                var signature = BinaryRead.At(file, peOffset, 24);
                if (BinaryRead.U32(signature, 0) != 0x4550) throw new InvalidDataException("Not a PE image.");
                var sectionCount = BinaryRead.U16(signature, 6);
                var optionalSize = BinaryRead.U16(signature, 20);
                var optional = BinaryRead.At(file, peOffset + 24L, optionalSize);
                var magic = BinaryRead.U16(optional, 0);
                var dataDirectory = magic == 0x20b ? 112 : magic == 0x10b ? 96 : throw new InvalidDataException("Unknown PE optional header.");
                if (optional.Length < dataDirectory + 7 * 8) throw new InvalidDataException("Truncated PE optional header.");
                var result = new PeIdentity { TimeDateStamp = BinaryRead.U32(signature, 8), SizeOfImage = BinaryRead.U32(optional, 56), CheckSum = BinaryRead.U32(optional, 64) };
                var debugRva = BinaryRead.U32(optional, dataDirectory + 6 * 8);
                var debugSize = BinaryRead.U32(optional, dataDirectory + 6 * 8 + 4);
                if (debugRva == 0 || debugSize < 28) return result;
                var sections = BinaryRead.At(file, peOffset + 24L + optionalSize, sectionCount * 40);
                long debugOffset = -1;
                for (var i = 0; i < sectionCount; i++)
                {
                    var o = i * 40;
                    var virtualSize = BinaryRead.U32(sections, o + 8);
                    var virtualAddress = BinaryRead.U32(sections, o + 12);
                    var rawSize = BinaryRead.U32(sections, o + 16);
                    var rawOffset = BinaryRead.U32(sections, o + 20);
                    if (debugRva >= virtualAddress && (ulong)debugRva + debugSize <= (ulong)virtualAddress + Math.Max(virtualSize, rawSize))
                    {
                        debugOffset = (long)rawOffset + debugRva - virtualAddress;
                        break;
                    }
                }
                if (debugOffset < 0) return result;
                var entries = BinaryRead.At(file, debugOffset, checked((int)debugSize));
                for (var i = 0; i + 28 <= entries.Length; i += 28)
                {
                    if (BinaryRead.U32(entries, i + 12) != 2) continue;
                    var size = BinaryRead.U32(entries, i + 16);
                    var rawOffset = BinaryRead.U32(entries, i + 24);
                    if (size < 25 || size > 32768) continue;
                    result.Pdb = BinaryRead.Rsds(BinaryRead.At(file, rawOffset, (int)size));
                    if (result.Pdb != null) break;
                }
                return result;
            }
        }
    }
}
