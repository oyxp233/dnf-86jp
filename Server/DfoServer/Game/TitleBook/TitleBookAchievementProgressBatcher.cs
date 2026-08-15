using DfoServer.Infrastructure;
using DfoServer.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Game.TitleBook
{
    internal sealed class TitleBookAchievementProgressBatcher
    {
        private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(100);

        private sealed class PendingProgress
        {
            internal EnhancedClientSession Session;
            internal int CharacterId;
            internal readonly Dictionary<int, AchievementTriggerResult> Results =
                new Dictionary<int, AchievementTriggerResult>();
            internal int Version;
            internal ClockService.ClockTimerHandle Timer;
        }

        private sealed class ProgressSnapshot
        {
            internal EnhancedClientSession Session;
            internal int CharacterId;
            internal IReadOnlyList<AchievementTriggerResult> Results;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<Guid, PendingProgress> _pending =
            new Dictionary<Guid, PendingProgress>();
        private readonly Func<
            EnhancedClientSession,
            int,
            IReadOnlyList<AchievementTriggerResult>,
            Task> _flush;

        internal TitleBookAchievementProgressBatcher(
            Func<
                EnhancedClientSession,
                int,
                IReadOnlyList<AchievementTriggerResult>,
                Task> flush)
        {
            _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        }

        internal void Queue(
            EnhancedClientSession session,
            IEnumerable<AchievementTriggerResult> results)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0 || results == null)
                return;

            var incoming = results.Where(result => result?.Success == true).ToList();
            if (incoming.Count == 0)
                return;

            lock (_sync)
            {
                if (!_pending.TryGetValue(session.SessionId, out var pending)
                    || pending.CharacterId != session.Player.CharacterId)
                {
                    pending?.Timer?.Cancel();
                    pending = new PendingProgress
                    {
                        Session = session,
                        CharacterId = session.Player.CharacterId,
                    };
                    _pending[session.SessionId] = pending;
                }

                foreach (var result in incoming)
                    MergeResult(pending.Results, result);

                pending.Version = NextVersion(pending.Version);
                var version = pending.Version;
                pending.Timer = ClockService.Instance.ScheduleOneShotAfterAsync(
                    BuildTimerName(session),
                    FlushDelay,
                    _ => FlushScheduledAsync(session.SessionId, version));
            }
        }

        internal async Task<bool> FlushPendingAsync(EnhancedClientSession session)
        {
            if (session == null)
                return false;

            ProgressSnapshot snapshot;
            ClockService.ClockTimerHandle timer;
            lock (_sync)
            {
                if (!_pending.TryGetValue(session.SessionId, out var pending))
                    return false;

                _pending.Remove(session.SessionId);
                timer = pending.Timer;
                snapshot = CreateSnapshot(pending);
            }

            timer?.Cancel();
            await FlushSnapshotAsync(snapshot);
            return true;
        }

        private async Task FlushScheduledAsync(Guid sessionId, int version)
        {
            ProgressSnapshot snapshot;
            lock (_sync)
            {
                if (!_pending.TryGetValue(sessionId, out var pending)
                    || pending.Version != version)
                {
                    return;
                }

                _pending.Remove(sessionId);
                snapshot = CreateSnapshot(pending);
            }

            await FlushSnapshotAsync(snapshot);
        }

        private async Task FlushSnapshotAsync(ProgressSnapshot snapshot)
        {
            if (snapshot?.Session?.Player == null
                || snapshot.Session.Player.CharacterId != snapshot.CharacterId)
            {
                return;
            }

            try
            {
                await _flush(
                    snapshot.Session,
                    snapshot.CharacterId,
                    snapshot.Results);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GameProtocol] ACHIEVEMENT_USE_ITEM batch flush failed: {ex.Message}");
            }
        }

        private static void MergeResult(
            IDictionary<int, AchievementTriggerResult> target,
            AchievementTriggerResult incoming)
        {
            if (!target.TryGetValue(incoming.QuestId, out var current)
                || !current.Completed
                || incoming.Completed)
            {
                target[incoming.QuestId] = Clone(incoming);
            }
        }

        private static ProgressSnapshot CreateSnapshot(PendingProgress pending)
            => new ProgressSnapshot
            {
                Session = pending.Session,
                CharacterId = pending.CharacterId,
                Results = pending.Results.Values
                    .OrderBy(result => result.QuestId)
                    .Select(Clone)
                    .ToArray(),
            };

        private static AchievementTriggerResult Clone(AchievementTriggerResult source)
            => new AchievementTriggerResult
            {
                Success = source.Success,
                ErrorCode = source.ErrorCode,
                QuestId = source.QuestId,
                Remain1 = source.Remain1,
                Remain2 = source.Remain2,
                Remain3 = source.Remain3,
                TailOrState = source.TailOrState,
                Completed = source.Completed,
                Category = source.Category,
                BookIndex = source.BookIndex,
                TitleItemId = source.TitleItemId,
            };

        private static int NextVersion(int version)
        {
            version = unchecked(version + 1);
            return version == 0 ? 1 : version;
        }

        private static string BuildTimerName(EnhancedClientSession session)
            => "titlebook-achievement:" + session.SessionId.ToString("N") + ":progress";
    }
}
