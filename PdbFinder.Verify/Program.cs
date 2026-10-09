using PdbFinder.Core;

if (args.Length == 1 && args[0] == "--self-test")
{
    IndexLookupChecks.Run();
    return;
}

if (args.Length == 0)
{
    Console.WriteLine("Usage: PdbFinder.Verify <dump> [pdb] [exe]");
    return;
}

var module = MinidumpReader.MainExecutable(args[0]);
if (module == null) throw new Exception("No EXE module was found.");
Console.WriteLine($"Module: {module.Name}");
Console.WriteLine($"Expected PDB: {module.ExpectedPdbName}");
Console.WriteLine($"PDB ID: {module.Pdb}");
Console.WriteLine($"PE timestamp: {module.TimeDateStamp:X8}; image size: {module.SizeOfImage}; checksum: {module.CheckSum:X8}");
if (args.Length > 1 && Directory.Exists(args[1]))
{
    var cache = Path.Combine(Path.GetTempPath(), "pdb-finder-verify-index.tsv");
    var roots = args[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var result = new CandidateFinder(cache).Find(roots, module, CancellationToken.None, Console.WriteLine);
    Console.WriteLine($"Visited {result.DirectoriesVisited} directories and inspected {result.PdbsInspected} PDBs.");
    Console.WriteLine($"Found PDB: {result.Candidate?.PdbPath ?? "none"}");
    Console.WriteLine($"Found EXE: {result.Candidate?.ExePath ?? "none"} ({result.Candidate?.Verification.ToString() ?? "-"})");
    if (result.Candidate?.Verification == PeVerification.Repackaged)
    {
        var pe = PeReader.Read(result.Candidate.ExePath!);
        Console.WriteLine($"EXE note: timestamp and PDB ID match, but image size {pe.SizeOfImage} != dump {module.SizeOfImage} (post-link repackaging).");
    }
    if (!string.IsNullOrEmpty(result.Error)) Console.WriteLine($"Error: {result.Error}");
}
else if (args.Length > 1 && File.Exists(args[1])) Console.WriteLine($"Candidate PDB ID: {PdbReader.Read(args[1])}");
else if (args.Length > 1)
{
    var cache = Path.Combine(Path.GetTempPath(), "pdb-finder-verify-index.tsv");
    var roots = args[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var result = new CandidateFinder(cache).Find(roots, module, CancellationToken.None, Console.WriteLine);
    Console.WriteLine($"Found PDB: {result.Candidate?.PdbPath ?? "none"}");
    Console.WriteLine($"Error: {result.Error ?? "none"}");
}
if (args.Length > 2)
{
    var pe = PeReader.Read(args[2]);
    Console.WriteLine($"Candidate EXE: timestamp {pe.TimeDateStamp:X8}; image size {pe.SizeOfImage}; checksum {pe.CheckSum:X8}; PDB ID {pe.Pdb}");
}
