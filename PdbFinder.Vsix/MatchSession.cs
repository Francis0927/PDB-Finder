#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using PdbFinder.Core;

namespace PdbFinder.Vsix
{
    // Owns a dump's entire operation, including copies that outlive archive lookup.
    // Retiring cancels all work; the source is disposed only after every worker exits.
    internal sealed class MatchSession
    {
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private int _operations;
        private bool _retired;
        private bool _disposed;

        public MatchSession(string dumpPath)
        {
            DumpPath = dumpPath;
            Token = _cancellation.Token;
        }

        public string DumpPath { get; }
        public CancellationToken Token { get; }
        public DumpModule? Module { get; set; }
        public PdbCandidate? Candidate { get; set; }
        public string? StagedDirectory { get; set; }
        public bool IsMatching { get; set; } = true;
        public bool IsStaging { get; set; }
        public bool CancelledByUser { get; set; }

        public void Cancel()
        {
            lock (_gate) { if (!_disposed) _cancellation.Cancel(); }
        }

        public void Retire()
        {
            lock (_gate)
            {
                if (_retired) return;
                _retired = true;
                _cancellation.Cancel();
                DisposeIfIdle();
            }
        }

        public async Task RunAsync(Func<Task> operation)
        {
            lock (_gate)
            {
                if (_retired) throw new OperationCanceledException(Token);
                _operations++;
            }
            try { await operation().ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    _operations--;
                    DisposeIfIdle();
                }
            }
        }

        private void DisposeIfIdle()
        {
            if (!_retired || _operations != 0 || _disposed) return;
            _disposed = true;
            _cancellation.Dispose();
        }
    }
}
