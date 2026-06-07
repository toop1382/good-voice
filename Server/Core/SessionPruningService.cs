using System;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Core
{
    public class SessionPruningService
    {
        private readonly RoomManager _roomManager;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(2);
        private readonly TimeSpan _timeoutThreshold = TimeSpan.FromSeconds(10);
        private CancellationTokenSource? _cts;

        public SessionPruningService(RoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => PruneLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private async Task PruneLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(_checkInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    PruneInactiveClients();
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation on server shutdown
            }
        }

        private void PruneInactiveClients()
        {
            long currentTicks = DateTime.UtcNow.Ticks;
            long thresholdTicks = _timeoutThreshold.Ticks;

            var sessions = _roomManager.GetAllSessions();

            foreach (var session in sessions)
            {
                long elapsedTicks = currentTicks - session.LastActivityTicks;
                if (elapsedTicks > thresholdTicks)
                {
                    Console.WriteLine($"[Prune] Client {session.ClientId} inactive for {TimeSpan.FromTicks(elapsedTicks).TotalSeconds:F1}s. Pruning session.");
                    _roomManager.UnregisterClient(session.ClientId, currentTicks, thresholdTicks);
                }
            }
        }
    }
}
