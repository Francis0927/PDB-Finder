using System.Collections.Concurrent;
using System.Text;
using PdbFinder.Core;

// Local binary fixtures exercise cache safety and fallback without accessing the archive.
internal static class IndexLookupChecks
{
    private static readonly Guid BuildGuid = new Guid("1851125c-f14b-49b1-939a-e79261b0e61d");
    private const uint Age = 2;
    private const uint Timestamp = 0x12345678;
    private const uint ImageSize = 0x2000;

    public static void Run()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var workspace = Path.GetFullPath(Path.Combine(tempRoot, "PdbFinderChecks-" + Guid.NewGuid().ToString("N")));
        if (!workspace.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid fixture directory.");
        Directory.CreateDirectory(workspace);
        try
        {
            var dump = Path.Combine(workspace, "test.dmp");
            WriteDump(dump);
            var module = MinidumpReader.MainExecutable(dump)!;
            var root = Path.Combine(workspace, "archive");
            var build = Path.Combine(root, "20261009");
            Directory.CreateDirectory(build);
            var pdb = Path.Combine(build, "Renamed.pdb");
            WritePdb(pdb, BuildGuid, Age);
            WriteExe(Path.Combine(build, "Game.exe"));
            var cache = Path.Combine(workspace, "index.tsv");

            var localStore = Path.Combine(workspace, "symbol-cache");
            var localPdb = LocalSymbolCache.GetStagedPdbPath(localStore, module);
            var localExe = LocalSymbolCache.GetStagedExePath(localStore, module);
            Directory.CreateDirectory(Path.GetDirectoryName(localPdb)!);
            Directory.CreateDirectory(Path.GetDirectoryName(localExe)!);
            File.Copy(pdb, localPdb);
            File.Copy(Path.Combine(build, "Game.exe"), localExe);
            var localHit = LocalSymbolCache.FindLocal(module, new[] { localStore }, null, CancellationToken.None, p =>
            {
                Check(p.Phase == IndexPhase.CheckingLocalSymbols, "Local lookup must only report local locations.");
            });
            Check(localHit?.PdbPath == localPdb && localHit.ExePath == localExe && localHit.Verification == PeVerification.Exact,
                "A local symbol-cache hit must include the separately cached executable without requiring an index or shared root.");
            Console.WriteLine("PASS: local PDB and EXE cache hit without an index or shared root.");

            var fallbackStore = Path.Combine(workspace, "fallback-cache");
            Directory.CreateDirectory(fallbackStore);
            var flatPdb = Path.Combine(fallbackStore, module.ExpectedPdbName);
            File.Copy(pdb, flatPdb);
            Check(LocalSymbolCache.FindLocal(module, new[] { Path.Combine(workspace, "absent-cache"), fallbackStore }, null,
                CancellationToken.None)?.PdbPath == flatPdb, "Missing primary cache must fall through to the fallback local cache.");
            Check(LocalSymbolCache.FindLocal(module, Array.Empty<string>(), fallbackStore, CancellationToken.None)?.PdbPath == flatPdb,
                "Local dump-adjacent symbols must be usable without a configured store.");
            Check(LocalSymbolCache.FindLocal(module, new[] { @"\\unreachable-server\symbols" }, @"\\unreachable-server\dump",
                CancellationToken.None) == null, "Local lookup must skip UNC stores and UNC dump directories.");
            Console.WriteLine("PASS: fallback/flat/adjacent local symbols; remote paths excluded from local lookup.");

            WritePdb(localPdb, Guid.NewGuid(), Age);
            Check(LocalSymbolCache.FindLocal(module, new[] { localStore }, null, CancellationToken.None) == null,
                "Invalid local PDB identity must not be accepted, even at the expected cache key.");
            WritePdb(localPdb, BuildGuid, Age + 1);
            Check(LocalSymbolCache.FindLocal(module, new[] { localStore }, null, CancellationToken.None) == null,
                "Invalid local PDB Age must not be accepted.");
            SeedIndex(cache, root, pdb);
            Check(new CandidateFinder(cache).Find(root, module, CancellationToken.None).FromIndex,
                "A local miss must allow the index lookup to succeed.");
            Console.WriteLine("PASS: invalid local GUID/Age falls through to index lookup.");

            using (var localCancellation = new CancellationTokenSource())
            {
                try
                {
                    LocalSymbolCache.FindLocal(module, new[] { localStore }, null, localCancellation.Token,
                        p => localCancellation.Cancel());
                    throw new Exception("Local cancellation was ignored.");
                }
                catch (OperationCanceledException) { }
            }
            Console.WriteLine("PASS: local lookup honors cancellation.");

            var copyStore = Path.Combine(workspace, "copy-cache");
            var copyProgress = new List<(long Copied, long Total)>();
            var candidateToCopy = new PdbCandidate(pdb, null);
            var copiedDirectory = LocalSymbolCache.StageAsync(copyStore, module, candidateToCopy, CancellationToken.None,
                (copied, total) => copyProgress.Add((copied, total))).GetAwaiter().GetResult();
            var copiedPdb = LocalSymbolCache.GetStagedPdbPath(copyStore, module);
            var sourceLength = new FileInfo(pdb).Length;
            Check(copyProgress.First() == (0, sourceLength) && copyProgress.Last() == (sourceLength, sourceLength),
                "Copy progress must report both preparation and the final validation phase, including for small files.");
            Check(copiedDirectory == Path.GetDirectoryName(copiedPdb) && File.ReadAllBytes(pdb).SequenceEqual(File.ReadAllBytes(copiedPdb)),
                "Staging must publish the complete PDB under the expected name.");
            var copiedAgain = false;
            LocalSymbolCache.StageAsync(copyStore, module, candidateToCopy, CancellationToken.None,
                (copied, total) => copiedAgain = true).GetAwaiter().GetResult();
            Check(!copiedAgain, "A valid staged copy must be reused without copying again.");
            Console.WriteLine("PASS: copy reports initial/final progress, publishes verified symbols and reuses existing copies.");

            var cancelledCopyStore = Path.Combine(workspace, "cancelled-copy-cache");
            using (var copyCancellation = new CancellationTokenSource())
            {
                try
                {
                    LocalSymbolCache.StageAsync(cancelledCopyStore, module, candidateToCopy, copyCancellation.Token, (copied, total) =>
                    {
                        if (copied == total && total > 0) copyCancellation.Cancel();
                    }).GetAwaiter().GetResult();
                    throw new Exception("Copy cancellation was ignored before publication.");
                }
                catch (OperationCanceledException) { }
            }
            Check(!File.Exists(LocalSymbolCache.GetStagedPdbPath(cancelledCopyStore, module)),
                "Cancelling a fully transferred but unvalidated copy must not publish it.");
            Console.WriteLine("PASS: cancelling at 100% transfer prevents symbol publication.");

            var invalidCopyStore = Path.Combine(workspace, "invalid-copy-cache");
            var invalidPdb = Path.Combine(workspace, "Invalid.pdb");
            WritePdb(invalidPdb, Guid.NewGuid(), Age);
            try
            {
                LocalSymbolCache.StageAsync(invalidCopyStore, module, new PdbCandidate(invalidPdb, null), CancellationToken.None)
                    .GetAwaiter().GetResult();
                throw new Exception("An invalid copy was published.");
            }
            catch (InvalidDataException) { }
            Check(!File.Exists(LocalSymbolCache.GetStagedPdbPath(invalidCopyStore, module)), "Failed PDB validation must not publish a symbol file.");
            Console.WriteLine("PASS: invalid copied PDB identity fails without publishing symbols.");

            // Old broad root metadata must not force a rebuild after the user narrows roots.
            SeedIndex(cache, workspace, pdb);
            var hit = new CandidateFinder(cache).Find(root, module, CancellationToken.None, indexProgress: p =>
            {
                Check(p.Phase == IndexPhase.CheckingIndex, "An index hit must not enumerate any shared directory.");
            });
            Check(hit.FromIndex && hit.DirectoriesVisited == 0 && hit.Candidate?.PdbPath == pdb
                && hit.Candidate.Verification == PeVerification.Exact, "Index hit must validate the PDB and EXE.");
            Console.WriteLine("PASS: narrowed roots reuse index; renamed PDB loads with zero directory scans.");

            File.Delete(cache);
            var phases = new ConcurrentQueue<IndexPhase>();
            var scanned = new CandidateFinder(cache).Find(root, module, CancellationToken.None, indexProgress: p => phases.Enqueue(p.Phase));
            Check(!scanned.FromIndex && scanned.Candidate?.PdbPath == pdb, "Missing index must fall back to scanning.");
            Check(phases.Contains(IndexPhase.CheckingIndex) && phases.Contains(IndexPhase.CollectingDirectories)
                && phases.Contains(IndexPhase.MatchingRecentPdbs), "Progress must include lookup, enumeration and recent matching.");
            Check(new CandidateFinder(cache).Find(root, module, CancellationToken.None).FromIndex, "Scan must save an index for the next lookup.");
            Console.WriteLine("PASS: missing index scans with progress and persists the result.");

            SeedIndex(cache, root, pdb);
            File.Delete(pdb);
            var replacement = Path.Combine(build, "OtherName.pdb");
            WritePdb(replacement, BuildGuid, Age);
            var removed = new CandidateFinder(cache).Find(root, module, CancellationToken.None);
            Check(!removed.FromIndex && removed.Candidate?.PdbPath == replacement, "Deleted indexed PDB must fall back to finding its replacement.");
            Console.WriteLine("PASS: deleted index candidate falls back to a renamed replacement.");

            SeedIndex(cache, root, replacement);
            var writeTime = File.GetLastWriteTimeUtc(replacement);
            WritePdb(replacement, Guid.NewGuid(), Age);
            File.SetLastWriteTimeUtc(replacement, writeTime);
            WritePdb(pdb, BuildGuid, Age);
            var replaced = new CandidateFinder(cache).Find(root, module, CancellationToken.None);
            Check(!replaced.FromIndex && replaced.Candidate?.PdbPath == pdb, "Changed PDB with identical length/time must be rejected.");
            Console.WriteLine("PASS: index lookup revalidates identity even with unchanged file metadata.");

            var sibling = root + "-other";
            Directory.CreateDirectory(sibling);
            var outside = Path.Combine(sibling, "Game.pdb");
            WritePdb(outside, BuildGuid, Age);
            SeedIndex(cache, workspace, outside);
            var restricted = new CandidateFinder(cache).Find(root + Path.DirectorySeparatorChar, module, CancellationToken.None);
            Check(!restricted.FromIndex && restricted.Candidate?.PdbPath == pdb, "Root prefix must not accept sibling directories.");
            Console.WriteLine("PASS: index candidates outside the current roots are excluded.");

            SeedIndex(cache, root, replacement, BuildGuid, Age);
            WritePdb(replacement, BuildGuid, Age + 1);
            var ageMismatch = new CandidateFinder(cache).Find(root, module, CancellationToken.None);
            Check(!ageMismatch.FromIndex && ageMismatch.Candidate?.PdbPath == pdb, "Same GUID with another Age must not match.");
            Console.WriteLine("PASS: stale Age does not match.");

            File.Delete(Path.Combine(build, "Game.exe"));
            SeedIndex(cache, root, pdb);
            var pdbOnly = new CandidateFinder(cache).Find(root, module, CancellationToken.None);
            Check(pdbOnly.FromIndex && pdbOnly.Candidate?.ExePath == null, "PDB-only index hits must remain usable.");
            Console.WriteLine("PASS: PDB-only index hit succeeds without a directory scan.");

            foreach (var cancelPhase in new[] { IndexPhase.CheckingIndex, IndexPhase.CollectingDirectories, IndexPhase.MatchingRecentPdbs })
            {
                SeedIndex(cache, root, cancelPhase == IndexPhase.CheckingIndex ? pdb : outside);
                var original = File.ReadAllBytes(cache);
                using var cancellation = new CancellationTokenSource();
                try
                {
                    new CandidateFinder(cache).Find(root, module, cancellation.Token, indexProgress: p =>
                    {
                        if (p.Phase == cancelPhase) cancellation.Cancel();
                    });
                    throw new Exception("Cancellation was ignored in " + cancelPhase);
                }
                catch (OperationCanceledException) { }
                Check(original.SequenceEqual(File.ReadAllBytes(cache)), "Cancellation must preserve the published index.");
            }
            Console.WriteLine("PASS: cancellation during lookup, enumeration and matching preserves the index.");
            RunRecentSearchChecks(workspace, module);
            RunDesignChecks(workspace, module);
        }
        finally
        {
            // Only the unique, verified fixture directory is removed.
            if (workspace.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(workspace).StartsWith("PdbFinderChecks-", StringComparison.Ordinal))
                Directory.Delete(workspace, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void RunRecentSearchChecks(string workspace, DumpModule module)
    {
        var root = Path.Combine(workspace, "production", "nei", "windows");
        var execute = Path.Combine(root, "qc_publish", "execute");
        var builds = Enumerable.Range(0, 65).Select(i => Path.Combine(execute, "build-" + (65 - i).ToString("D3"))).ToArray();
        var latest = Path.Combine(builds[64], "symbols", "nested");
        Directory.CreateDirectory(latest);
        var latestPdb = Path.Combine(latest, "Renamed.pdb");
        WritePdb(latestPdb, BuildGuid, Age);
        foreach (var build in builds)
        {
            Directory.CreateDirectory(build);
            if (build != builds[64]) WritePdb(Path.Combine(build, "Old.pdb"), Guid.NewGuid(), Age);
        }
        // Timestamp order deliberately opposes the directory names; set it after writing files.
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < builds.Length; i++) Directory.SetLastWriteTimeUtc(builds[i], epoch.AddDays(i));
        var cache = Path.Combine(workspace, "recent-search.tsv");
        var inspectedPaths = new ConcurrentQueue<string>();
        var phases = new ConcurrentQueue<IndexPhase>();
        var recent = new CandidateFinder(cache).Find(root, module, CancellationToken.None, indexProgress: p =>
        {
            phases.Enqueue(p.Phase);
            if (p.CurrentPath != null) inspectedPaths.Enqueue(p.CurrentPath);
        });
        Check(recent.Candidate?.PdbPath == latestPdb && recent.Candidate.ExePath == null
            && !phases.Contains(IndexPhase.MatchingPdbs), "A recent nested PDB-only hit must avoid history expansion.");
        Check(!inspectedPaths.Any(path => builds.Take(35).Any(build => path == build || path.StartsWith(build + Path.DirectorySeparatorChar)))
            && recent.PdbsInspected == 30, "Only the latest 30 build subtrees may be opened on a recent hit.");
        Check(new CandidateFinder(cache).Find(root, module, CancellationToken.None).FromIndex,
            "A partial recent scan must save the match for index reuse.");
        Console.WriteLine("PASS: recent-30 search uses timestamps, reaches nested/renamed PDBs and avoids old subtrees.");

        var secondRoot = Path.Combine(workspace, "production", "wai", "windows");
        var secondBuild = Path.Combine(secondRoot, "qc_publish", "execute", "new-build", "symbols");
        Directory.CreateDirectory(secondBuild);
        var secondPdb = Path.Combine(secondBuild, "Second.pdb");
        WritePdb(secondPdb, BuildGuid, Age);
        WritePdb(latestPdb, Guid.NewGuid(), Age);
        var historicalPdb = Path.Combine(builds[34], "Old.pdb");
        WritePdb(historicalPdb, BuildGuid, Age);
        WriteExe(Path.Combine(builds[34], "Game.exe"));
        Directory.SetLastWriteTimeUtc(builds[34], epoch.AddDays(34));
        phases.Clear();
        var multiple = new CandidateFinder(Path.Combine(workspace, "multiple-recent.tsv")).Find(new[] { root, secondRoot }, module,
            CancellationToken.None, indexProgress: p => phases.Enqueue(p.Phase));
        Check(multiple.Candidate?.PdbPath == secondPdb && !phases.Contains(IndexPhase.MatchingPdbs),
            "Recent candidates in every configured root must precede historical fallback.");
        Console.WriteLine("PASS: multiple roots receive a recent search before historical fallback.");

        phases.Clear();
        var history = new CandidateFinder(Path.Combine(workspace, "history-search.tsv")).Find(root, module, CancellationToken.None,
            indexProgress: p => phases.Enqueue(p.Phase));
        Check(history.Candidate?.PdbPath == historicalPdb && phases.Contains(IndexPhase.MatchingRecentPdbs)
            && phases.Contains(IndexPhase.MatchingPdbs), "The 31st build must be found after the recent 30 miss.");

        var sibling = Path.Combine(root, "other-channel", "execute", "old-build");
        Directory.CreateDirectory(sibling);
        var siblingPdb = Path.Combine(sibling, "Sibling.pdb");
        WritePdb(siblingPdb, BuildGuid, Age);
        WritePdb(historicalPdb, Guid.NewGuid(), Age);
        Directory.SetLastWriteTimeUtc(builds[34], epoch.AddDays(34));
        var siblingHit = new CandidateFinder(Path.Combine(workspace, "sibling-search.tsv")).Find(root, module, CancellationToken.None);
        Check(siblingHit.Candidate?.PdbPath == siblingPdb, "Search must expand to sibling channels within the configured root.");
        WritePdb(siblingPdb, Guid.NewGuid(), Age);
        var expectedDirectories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length + 1;
        var absent = new CandidateFinder(Path.Combine(workspace, "absent-recent.tsv")).Find(
            new[] { root, root + Path.DirectorySeparatorChar, execute + Path.DirectorySeparatorChar }, module, CancellationToken.None);
        Check(absent.Candidate == null && absent.DirectoriesVisited == expectedDirectories && absent.PdbsInspected == 66,
            "A complete miss must scan all recent and historical directories/PDBs exactly once, including overlapping roots.");
        Console.WriteLine("PASS: recent misses expand to history and sibling channels; full fallback visits each directory/PDB once.");

        var rebuilt = new CandidateFinder(Path.Combine(workspace, "recent-rebuild.tsv")).RebuildIndex(root, CancellationToken.None);
        Check(rebuilt.DirectoriesVisited == expectedDirectories && rebuilt.PdbsInspected == 66,
            "Manual rebuilding must still index the entire archive.");
        var original = File.ReadAllBytes(cache);
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                new CandidateFinder(cache).Find(root, module, cancellation.Token, indexProgress: p =>
                {
                    if (p.Phase == IndexPhase.MatchingPdbs) cancellation.Cancel();
                });
                throw new Exception("Historical fallback ignored cancellation.");
            }
            catch (OperationCanceledException) { }
        }
        Check(original.SequenceEqual(File.ReadAllBytes(cache)), "Cancelling historical fallback must preserve the published index.");
        Console.WriteLine("PASS: manual rebuild stays complete; cancelling historical fallback preserves the index.");
    }

    private static void RunDesignChecks(string workspace, DumpModule module)
    {
        var root = Path.Combine(workspace, "mixed-layout");
        var build = Path.Combine(root, "channel", "execute", "build");
        var loose = Path.Combine(root, "loose", "symbols");
        Directory.CreateDirectory(build);
        Directory.CreateDirectory(loose);
        var pdb = Path.Combine(loose, "Renamed.pdb");
        WritePdb(pdb, BuildGuid, Age);
        var mixed = new CandidateFinder(Path.Combine(workspace, "mixed.tsv")).Find(root, module, CancellationToken.None);
        Check(mixed.Candidate?.PdbPath == pdb, "Recognizing execute/build must not exclude other archive directories.");
        var nested = Path.Combine(build, "nested", "symbols");
        Directory.CreateDirectory(nested);
        var nestedPdb = Path.Combine(nested, "Nested.pdb");
        File.Move(pdb, nestedPdb);
        var nestedHit = new CandidateFinder(Path.Combine(workspace, "nested.tsv")).Find(new[] { root, build }, module, CancellationToken.None);
        var directoryCount = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length + 1;
        Check(nestedHit.Candidate?.PdbPath == nestedPdb && nestedHit.DirectoriesVisited == directoryCount,
            "Nested symbols and overlapping roots must be scanned completely without duplicate directory visits.");
        Console.WriteLine("PASS: mixed layouts, nested build symbols and overlapping roots are covered.");

        var cache = Path.Combine(workspace, "stale-negative.tsv");
        SeedIndex(cache, root, nestedPdb, Guid.NewGuid());
        var refreshed = new CandidateFinder(cache).Find(root, module, CancellationToken.None);
        Check(refreshed.Candidate?.PdbPath == nestedPdb && !refreshed.FromIndex && refreshed.DirectoriesVisited == directoryCount,
            "A stale negative identity with unchanged file metadata must be refreshed during the first scan.");
        WritePdb(nestedPdb, Guid.NewGuid(), Age);
        SeedIndex(cache, root, nestedPdb, Guid.NewGuid());
        var absent = new CandidateFinder(cache).Find(root, module, CancellationToken.None);
        Check(absent.Candidate == null && absent.DirectoriesVisited == directoryCount && absent.PdbsInspected == 1,
            "An absent build must inspect each directory/PDB once rather than rebuilding and rescanning.");
        WritePdb(nestedPdb, BuildGuid, Age);
        Console.WriteLine("PASS: stale negative metadata is refreshed; absent builds use one archive scan.");

        var failedSave = Path.Combine(workspace, "index-is-directory.tsv");
        Directory.CreateDirectory(failedSave);
        var found = new CandidateFinder(failedSave).Find(root, module, CancellationToken.None);
        Check(found.Candidate?.PdbPath == nestedPdb && found.IndexSaveFailed && found.Error?.Contains("索引保存失败") == true,
            "Cache publication failure must preserve the successful matching result and report a warning.");
        var rebuild = new CandidateFinder(failedSave).RebuildIndex(root, CancellationToken.None);
        Check(rebuild.IndexSaveFailed, "A failed manual index save must not be reported as a successful rebuild.");
        Console.WriteLine("PASS: failed index writes preserve matches and expose rebuild failure.");

        var locked = Path.Combine(workspace, "locked.tsv");
        SeedIndex(locked, root, nestedPdb);
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var fallback = new CandidateFinder(locked).Find(root, module, CancellationToken.None);
            Check(fallback.Candidate?.PdbPath == nestedPdb && !fallback.FromIndex && fallback.Error?.Contains("索引读取失败") == true,
                "An unreadable index must fall back to archive scanning.");
        }
        File.WriteAllText(locked, Convert.ToBase64String(Encoding.UTF8.GetBytes(nestedPdb))
            + "\t99999999999999999999999999999\t0\t" + BuildGuid + "\t2\nnot-base64\t0\t0\tbad-guid\t2");
        var corrupt = new CandidateFinder(locked).Find(root, module, CancellationToken.None);
        Check(corrupt.Candidate?.PdbPath == nestedPdb && corrupt.Error?.Contains("无效记录") == true,
            "Overflow and malformed index rows must not prevent matching.");
        Console.WriteLine("PASS: locked and corrupt indexes fall back without blocking matching.");

        var exe = Path.Combine(nested, "Game.exe");
        WriteExe(exe);
        var bytes = File.ReadAllBytes(exe);
        U32(bytes, 144, ImageSize + 0x1000);
        File.WriteAllBytes(exe, bytes);
        Check(CandidateFinder.VerifyPe(exe, module) == PeVerification.Repackaged, "Matching RSDS identity permits repackaged verification.");
        var patchStore = Path.Combine(workspace, "patched-store");
        var candidate = new PdbCandidate(nestedPdb, exe, PeVerification.Repackaged);
        LocalSymbolCache.StagePatchedExeAsync(patchStore, module, candidate, CancellationToken.None).GetAwaiter().GetResult();
        Check(LocalSymbolCache.IsExeStaged(patchStore, module), "A verified repackaged EXE can be patched and reused.");
        U32(bytes, 248, 0); U32(bytes, 252, 0); // Remove the debug directory: timestamp alone is insufficient.
        File.WriteAllBytes(exe, bytes);
        Check(CandidateFinder.VerifyPe(exe, module) == PeVerification.None, "Missing RSDS must not count as verified EXE identity.");
        var unsafeStore = Path.Combine(workspace, "unsafe-patch-store");
        try
        {
            LocalSymbolCache.StagePatchedExeAsync(unsafeStore, module, candidate, CancellationToken.None).GetAwaiter().GetResult();
            throw new Exception("An EXE without RSDS was patched.");
        }
        catch (InvalidDataException) { }
        Check(!File.Exists(LocalSymbolCache.GetStagedExePath(unsafeStore, module)), "An unverified EXE must never be published.");
        U32(bytes, 144, ImageSize);
        File.WriteAllBytes(exe, bytes);
        Check(CandidateFinder.VerifyPe(exe, module) == PeVerification.None, "Even exact PE fields cannot replace missing RSDS identity.");
        Directory.CreateDirectory(LocalSymbolCache.GetStagedExeDirectory(unsafeStore, module));
        File.Copy(exe, LocalSymbolCache.GetStagedExePath(unsafeStore, module));
        Check(!LocalSymbolCache.IsExeStaged(unsafeStore, module), "An old cached EXE without RSDS must not be reused.");
        WriteExe(exe);
        bytes = File.ReadAllBytes(exe);
        Guid.NewGuid().ToByteArray().CopyTo(bytes, 548);
        File.WriteAllBytes(exe, bytes);
        Check(CandidateFinder.VerifyPe(exe, module) == PeVerification.None, "A different EXE RSDS identity must be rejected.");
        var changedStore = Path.Combine(workspace, "changed-exe-store");
        try
        {
            LocalSymbolCache.StagePatchedExeAsync(changedStore, module, candidate, CancellationToken.None).GetAwaiter().GetResult();
            throw new Exception("An EXE replaced after candidate selection was published.");
        }
        catch (InvalidDataException) { }
        Check(!File.Exists(LocalSymbolCache.GetStagedExePath(changedStore, module)), "Changed EXE identity must be caught by copy validation.");
        WriteExe(exe);
        bytes = File.ReadAllBytes(exe);
        U32(bytes, 564, Age + 1);
        File.WriteAllBytes(exe, bytes);
        Check(CandidateFinder.VerifyPe(exe, module) == PeVerification.None, "An EXE with the wrong RSDS Age must be rejected.");
        Console.WriteLine("PASS: patching requires matching EXE GUID/Age before and after copying.");

        Check(LocalSymbolCache.IsLocalPath(workspace) && !LocalSymbolCache.IsLocalPath(@"\\unreachable-server\symbols")
            && !LocalSymbolCache.IsLocalPath("relative-cache"), "Only known local absolute paths may bypass staging.");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network))
            Check(!LocalSymbolCache.IsLocalPath(drive.Name), "Mapped network drives must not be considered local.");
        try
        {
            LocalSymbolCache.StageAsync(@"\\unreachable-server\symbols", module, new PdbCandidate(nestedPdb, null), CancellationToken.None)
                .GetAwaiter().GetResult();
            throw new Exception("A remote staging root was accepted.");
        }
        catch (ArgumentException) { }
        Console.WriteLine("PASS: local-path policy excludes UNC/relative roots and rejects remote staging.");

        var concurrentStore = Path.Combine(workspace, "concurrent-store");
        var copyCandidate = new PdbCandidate(nestedPdb, null);
        using var copiesStarted = new CountdownEvent(4);
        Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            LocalSymbolCache.StageAsync(concurrentStore, module, copyCandidate, CancellationToken.None, (copied, total) =>
            {
                if (copied != 0 || total == 0) return;
                copiesStarted.Signal();
                Check(copiesStarted.Wait(TimeSpan.FromSeconds(10)), "All copies must overlap before publication.");
            })))).GetAwaiter().GetResult();
        Check(LocalSymbolCache.IsStaged(concurrentStore, module)
            && Directory.GetFiles(concurrentStore, "*.download", SearchOption.AllDirectories).Length == 0,
            "Concurrent staging must publish a valid PDB and remove each operation's temporary file.");
        var cancelStore = Path.Combine(workspace, "cancel-patch-store");
        WriteExe(exe);
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                LocalSymbolCache.StagePatchedExeAsync(cancelStore, module, new PdbCandidate(nestedPdb, exe, PeVerification.Exact), cancellation.Token,
                    (copied, total) => { if (copied == total && total > 0) cancellation.Cancel(); }).GetAwaiter().GetResult();
                throw new Exception("Cancelled EXE copy was published.");
            }
            catch (OperationCanceledException) { }
        }
        Check(!File.Exists(LocalSymbolCache.GetStagedExePath(cancelStore, module))
            && Directory.GetFiles(cancelStore, "*.download", SearchOption.AllDirectories).Length == 0,
            "Cancellation must prevent EXE publication and clean temporary files.");
        Console.WriteLine("PASS: concurrent staging and cancelled EXE copies leave no incomplete published files.");
        CheckSessionLifecycle().GetAwaiter().GetResult();
    }

    private static async Task CheckSessionLifecycle()
    {
        var session = new PdbFinder.Vsix.MatchSession("old.dmp");
        await session.RunAsync(() => Task.CompletedTask); // Lookup has finished, copies still belong to this session.
        var started = new CountdownEvent(2);
        var cancelled = 0;
        Func<Task> copy = async () =>
        {
            started.Signal();
            try { await Task.Delay(Timeout.Infinite, session.Token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); }
        };
        var pdbCopy = session.RunAsync(copy);
        var exeCopy = session.RunAsync(copy);
        Check(started.Wait(TimeSpan.FromSeconds(5)), "Both copy operations must have started.");
        var replacement = new PdbFinder.Vsix.MatchSession("new.dmp");
        session.Retire();
        await Task.WhenAll(pdbCopy, exeCopy);
        Check(cancelled == 2 && session.Token.IsCancellationRequested && !replacement.Token.IsCancellationRequested,
            "Replacing a dump after lookup must cancel both copies without cancelling the replacement.");
        session.Cancel(); session.Retire(); // Delayed close/cancel callbacks must remain safe after workers exit.
        await replacement.RunAsync(() => Task.CompletedTask);
        replacement.Retire();
        started.Dispose();
        Console.WriteLine("PASS: retiring a match session cancels PDB/EXE workers after lookup; late callbacks are safe.");
    }

    private static void SeedIndex(string cache, string root, string pdb, Guid? identity = null, uint age = Age)
    {
        var info = new FileInfo(pdb);
        var lines = new[]
        {
            "#roots=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(root)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(pdb)) + "\t" + info.Length + "\t" + info.LastWriteTimeUtc.Ticks
                + "\t" + (identity ?? BuildGuid).ToString("D") + "\t" + age
        };
        File.WriteAllLines(cache, lines);
    }

    private static void WritePdb(string path, Guid guid, uint age)
    {
        var data = new byte[2048];
        Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00").CopyTo(data, 0);
        U32(data, 32, 512); U32(data, 44, 16); U32(data, 52, 1);
        U32(data, 512, 2);
        U32(data, 1024, 2); U32(data, 1032, 28); U32(data, 1036, 3);
        U32(data, 1544, age); guid.ToByteArray().CopyTo(data, 1548);
        File.WriteAllBytes(path, data);
    }

    private static void WriteDump(string path)
    {
        var data = new byte[512];
        U32(data, 0, 0x504d444d); U32(data, 8, 1); U32(data, 12, 32);
        U32(data, 32, 4); U32(data, 36, 112); U32(data, 40, 64); U32(data, 64, 1);
        U32(data, 76, ImageSize); U32(data, 84, Timestamp); U32(data, 88, 192);
        var name = Encoding.Unicode.GetBytes("Game.exe");
        U32(data, 192, (uint)name.Length); name.CopyTo(data, 196);
        var cv = CodeView();
        U32(data, 144, (uint)cv.Length); U32(data, 148, 256); cv.CopyTo(data, 256);
        File.WriteAllBytes(path, data);
    }

    private static void WriteExe(string path)
    {
        var data = new byte[1024];
        data[0] = 0x4d; data[1] = 0x5a;
        U32(data, 60, 64); U32(data, 64, 0x4550); data[70] = 1;
        U32(data, 72, Timestamp); data[84] = 240; data[88] = 0x0b; data[89] = 0x02;
        U32(data, 144, ImageSize);
        U32(data, 248, 0x1000); U32(data, 252, 28);
        U32(data, 336, 256); U32(data, 340, 0x1000); U32(data, 344, 256); U32(data, 348, 512);
        var cv = CodeView();
        U32(data, 524, 2); U32(data, 528, (uint)cv.Length); U32(data, 536, 544); cv.CopyTo(data, 544);
        File.WriteAllBytes(path, data);
    }

    private static byte[] CodeView()
    {
        var data = new byte[33];
        Encoding.ASCII.GetBytes("RSDS").CopyTo(data, 0);
        BuildGuid.ToByteArray().CopyTo(data, 4);
        U32(data, 20, Age);
        Encoding.ASCII.GetBytes("Game.pdb").CopyTo(data, 24);
        return data;
    }

    private static void U32(byte[] data, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(data, offset);
}
