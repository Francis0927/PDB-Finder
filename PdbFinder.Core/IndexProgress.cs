namespace PdbFinder.Core
{
    public enum IndexPhase { CollectingDirectories, ScanningPdbs, SavingIndex, CheckingIndex, MatchingPdbs, CheckingLocalSymbols }

    // Immutable snapshots can be reported by parallel workers and displayed on the UI thread.
    public sealed class IndexProgress
    {
        public IndexProgress(IndexPhase phase, int completedDirectories, int totalDirectories,
            int pdbsInspected, int skippedDirectories, string currentPath)
        {
            Phase = phase;
            CompletedDirectories = completedDirectories;
            TotalDirectories = totalDirectories;
            PdbsInspected = pdbsInspected;
            SkippedDirectories = skippedDirectories;
            CurrentPath = currentPath;
        }

        public IndexPhase Phase { get; }
        public int CompletedDirectories { get; }
        public int TotalDirectories { get; }
        public int PdbsInspected { get; }
        public int SkippedDirectories { get; }
        public string CurrentPath { get; }
    }
}
