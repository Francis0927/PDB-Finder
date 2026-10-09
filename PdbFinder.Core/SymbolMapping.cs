using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PdbFinder.Core
{
    // VS's Module.LoadSymbols only accepts a LOCAL directory that contains the PDB
    // under the name recorded in the module (the dump's expected name). UNC paths are
    // resolved with symbol-store semantics and renamed PDBs never match, so a renamed
    // PDB must be staged locally under its expected name first.
    public static class LocalSymbolCache
    {
        // Only short local validation/publication steps are serialized; copies run in parallel.
        private static readonly object PublicationGate = new object();
        // Probe known local locations only. No shared directory or index is needed for a local hit.
        public static PdbCandidate FindLocal(DumpModule module, IReadOnlyList<string> storeRoots, string dumpDirectory,
            CancellationToken cancellationToken, Action<IndexProgress> progress = null)
        {
            if (module?.Pdb == null) throw new ArgumentException("The dump module has no RSDS PDB identity.", nameof(module));
            cancellationToken.ThrowIfCancellationRequested();
            var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in storeRoots ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(root) || !IsLocalPath(root)) continue;
                var fullRoot = Path.GetFullPath(root);
                locations[GetStagedPdbPath(fullRoot, module)] = fullRoot;
                locations[Path.Combine(fullRoot, module.ExpectedPdbName)] = fullRoot;
            }
            if (!string.IsNullOrWhiteSpace(dumpDirectory) && IsLocalPath(dumpDirectory))
            {
                var adjacent = Path.Combine(Path.GetFullPath(dumpDirectory), module.ExpectedPdbName);
                if (!locations.ContainsKey(adjacent)) locations[adjacent] = null;
            }
            var completed = 0;
            foreach (var location in locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(new IndexProgress(IndexPhase.CheckingLocalSymbols, completed, locations.Count, completed, 0, location.Key));
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(location.Key))
                {
                    try
                    {
                        if (module.Pdb.Equals(PdbReader.Read(location.Key)))
                        {
                            var exe = Path.Combine(Path.GetDirectoryName(location.Key), Path.GetFileName(module.Name));
                            var verification = CandidateFinder.VerifyPe(exe, module);
                            if (verification != PeVerification.Exact && location.Value != null && IsExeStaged(location.Value, module))
                            {
                                exe = GetStagedExePath(location.Value, module);
                                verification = PeVerification.Exact;
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                            return new PdbCandidate(location.Key, verification == PeVerification.None ? null : exe, verification);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (InvalidDataException) { }
                }
                completed++;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        public static bool IsLocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;
                // Treat unknown/disconnected drives as unavailable, rather than local.
                var type = new DriveInfo(Path.GetPathRoot(fullPath)).DriveType;
                return type == DriveType.Fixed || type == DriveType.Removable || type == DriveType.Ram || type == DriveType.CDRom;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        private static void RequireLocalStore(string storeRoot)
        {
            if (!IsLocalPath(storeRoot)) throw new ArgumentException("符号暂存目录必须位于本机磁盘。", nameof(storeRoot));
        }

        public static string GetStagedDirectory(string storeRoot, DumpModule module)
        {
            // Lowercase GUID+Age key, matching the symsrv cache layout (<name>\<key>\<file>),
            // so the engine's own cache lookups hit the staged file directly.
            return Path.Combine(storeRoot, module.ExpectedPdbName, module.Pdb.ToString().ToLowerInvariant());
        }

        public static string GetStagedPdbPath(string storeRoot, DumpModule module)
        {
            return Path.Combine(GetStagedDirectory(storeRoot, module), module.ExpectedPdbName);
        }

        // True when a staged PDB with the same identity already exists.
        public static bool IsStaged(string storeRoot, DumpModule module)
        {
            if (!IsLocalPath(storeRoot)) return false;
            lock (PublicationGate)
            {
                var staged = GetStagedPdbPath(storeRoot, module);
                if (!File.Exists(staged)) return false;
                try { return module.Pdb.Equals(PdbReader.Read(staged)); }
                catch (Exception) { return false; }
            }
        }

        // symsrv key for executables: <timestamp hex8><sizeOfImage hex> lowercase.
        public static string GetStagedExeDirectory(string storeRoot, DumpModule module)
        {
            var key = module.TimeDateStamp.ToString("x8") + module.SizeOfImage.ToString("x");
            return Path.Combine(storeRoot, Path.GetFileName(module.Name), key);
        }

        public static string GetStagedExePath(string storeRoot, DumpModule module)
        {
            return Path.Combine(GetStagedExeDirectory(storeRoot, module), Path.GetFileName(module.Name));
        }

        public static bool IsExeStaged(string storeRoot, DumpModule module)
        {
            if (!IsLocalPath(storeRoot)) return false;
            lock (PublicationGate)
            {
                var staged = GetStagedExePath(storeRoot, module);
                if (!File.Exists(staged)) return false;
                try
                {
                    var pe = PeReader.Read(staged);
                    return pe.TimeDateStamp == module.TimeDateStamp
                        && pe.SizeOfImage == module.SizeOfImage
                        && (module.CheckSum == 0 || pe.CheckSum == module.CheckSum)
                        && pe.Pdb != null && pe.Pdb.Equals(module.Pdb);
                }
                catch (Exception) { return false; }
            }
        }

        // Copies the (repackaged) archive EXE into the symbol cache and rewrites
        // SizeOfImage/CheckSum in the LOCAL copy to the dump module's recorded values,
        // so the VS binary check accepts it as the module image. The archive file
        // is never modified. Timestamp and the RSDS debug record are left untouched.
        public static async Task<string> StagePatchedExeAsync(string storeRoot, DumpModule module, PdbCandidate candidate, CancellationToken cancellationToken, Action<long, long> progress = null)
        {
            if (module?.Pdb == null || candidate?.ExePath == null) throw new ArgumentException("A verified module and EXE are required.");
            cancellationToken.ThrowIfCancellationRequested();
            RequireLocalStore(storeRoot);
            var directory = GetStagedExeDirectory(storeRoot, module);
            var staged = GetStagedExePath(storeRoot, module);
            if (IsExeStaged(storeRoot, module)) return directory;
            Directory.CreateDirectory(directory);
            var temp = staged + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                await CopyFileAsync(candidate.ExePath, temp, cancellationToken, progress).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // Revalidate the copied image before changing any identity fields. A cached
                // candidate or a missing RSDS record is not evidence that this EXE belongs to the dump.
                if (CandidateFinder.VerifyPe(temp, module) == PeVerification.None)
                    throw new InvalidDataException("EXE 时间戳或 PDB GUID/Age 不匹配，不能创建修补副本。");
                PatchPeIdentity(temp, module);
                var patched = PeReader.Read(temp);
                if (patched.TimeDateStamp != module.TimeDateStamp || patched.SizeOfImage != module.SizeOfImage
                    || patched.CheckSum != module.CheckSum || patched.Pdb == null || !patched.Pdb.Equals(module.Pdb))
                    throw new InvalidDataException("修补后的 EXE 校验失败：" + staged);
                PublishFile(temp, staged, cancellationToken, () => IsExeStaged(storeRoot, module));
                return directory;
            }
            finally { DeleteTemporaryFile(temp); }
        }

        private static void PatchPeIdentity(string path, DumpModule module)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var header = BinaryRead.At(file, 0, 64);
                if (BinaryRead.U16(header, 0) != 0x5a4d) throw new InvalidDataException("Not a PE image.");
                var peOffset = BinaryRead.U32(header, 60);
                var signature = BinaryRead.At(file, peOffset, 24);
                if (BinaryRead.U32(signature, 0) != 0x4550) throw new InvalidDataException("Not a PE image.");
                var optional = peOffset + 24;
                var magic = BinaryRead.U16(BinaryRead.At(file, optional, 2), 0);
                if (magic != 0x10b && magic != 0x20b) throw new InvalidDataException("Unknown PE optional header.");
                var fields = new byte[16];
                file.Position = optional + 56;
                file.Read(fields, 0, 16);
                BitConverter.GetBytes(module.SizeOfImage).CopyTo(fields, 0);
                BitConverter.GetBytes(module.CheckSum).CopyTo(fields, 8);
                file.Position = optional + 56;
                file.Write(fields, 0, 16);
            }
        }

        public static async Task<string> StageAsync(string storeRoot, DumpModule module, PdbCandidate candidate, CancellationToken cancellationToken, Action<long, long> progress = null)
        {
            if (module?.Pdb == null || candidate == null) throw new ArgumentException("A verified module and PDB are required.");
            cancellationToken.ThrowIfCancellationRequested();
            RequireLocalStore(storeRoot);
            // No copy needed only when a LOCAL directory already holds the PDB under the expected name.
            var isLocal = IsLocalPath(candidate.PdbPath);
            if (isLocal && string.Equals(Path.GetFileName(candidate.PdbPath), module.ExpectedPdbName, StringComparison.OrdinalIgnoreCase))
                return candidate.BuildDirectory;
            var directory = GetStagedDirectory(storeRoot, module);
            var staged = GetStagedPdbPath(storeRoot, module);
            if (IsStaged(storeRoot, module)) return directory;
            Directory.CreateDirectory(directory);
            var temp = staged + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                await CopyFileAsync(candidate.PdbPath, temp, cancellationToken, progress).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!module.Pdb.Equals(PdbReader.Read(temp)))
                    throw new InvalidDataException("复制后的 PDB 校验失败：" + staged);
                PublishFile(temp, staged, cancellationToken, () => IsStaged(storeRoot, module));
                return directory;
            }
            finally { DeleteTemporaryFile(temp); }
        }

        private static async Task CopyFileAsync(string sourcePath, string targetPath, CancellationToken token, Action<long, long> progress)
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.SequentialScan))
            using (var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.SequentialScan))
            {
                var length = source.Length;
                progress?.Invoke(0, length);
                token.ThrowIfCancellationRequested();
                var buffer = new byte[4 * 1024 * 1024];
                long copied = 0;
                var lastReport = Environment.TickCount;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (read == 0) break;
                    await target.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                    copied += read;
                    if (Environment.TickCount - lastReport >= 200)
                    {
                        lastReport = Environment.TickCount;
                        progress?.Invoke(copied, length);
                    }
                }
                progress?.Invoke(copied, length);
            }
        }

        private static void PublishFile(string temp, string staged, CancellationToken token, Func<bool> alreadyValid)
        {
            lock (PublicationGate)
            {
                token.ThrowIfCancellationRequested();
                // Another operation (or VS instance) may have finished this build meanwhile.
                // Keep its verified copy instead of replacing a file the debugger might be reading.
                if (alreadyValid()) return;
                token.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(staged)) File.Replace(temp, staged, null);
                    else File.Move(temp, staged);
                }
                catch (IOException) when (alreadyValid()) { }
            }
        }

        private static void DeleteTemporaryFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static class SymbolMapping
    {
        // SymSrv first looks up <name>\<GUID+Age>\file.ptr. The pointer contains
        // the actual file path, so a different on-disk PDB name is acceptable.
        public static string CreatePointerStore(string storeRoot, DumpModule module, PdbCandidate candidate)
        {
            if (module?.Pdb == null || candidate == null) throw new ArgumentException("A verified module and PDB are required.");
            var expected = module.ExpectedPdbName;
            if (string.IsNullOrWhiteSpace(expected) || !expected.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || expected.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || expected != Path.GetFileName(expected))
                throw new InvalidDataException("The dump contains an invalid PDB name.");
            var indexedDirectory = Path.Combine(storeRoot, expected, module.Pdb.ToString());
            Directory.CreateDirectory(indexedDirectory);
            File.WriteAllText(Path.Combine(indexedDirectory, "file.ptr"), candidate.PdbPath, Encoding.ASCII);
            return storeRoot;
        }

        public static string BuildSymbolPath(string currentPath, string pointerStore, PdbCandidate candidate)
        {
            var addition = "srv*" + pointerStore;
            if (candidate.HasVerifiedExe) addition = candidate.BuildDirectory + ";" + addition;
            return string.IsNullOrWhiteSpace(currentPath) ? addition : currentPath.TrimEnd(';') + ";" + addition;
        }
    }
}
