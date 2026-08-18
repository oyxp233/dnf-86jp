using System;
using System.Collections.Concurrent;
using DfoServer.Game.DailyReset;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class DailyChallengeService
    {
        private static readonly ConcurrentDictionary<long, byte> MissingEntryWarnings =
            new ConcurrentDictionary<long, byte>();

        private readonly DailyChallengeRepository _repository;
        private readonly string _connectionString;

        internal DailyChallengeService(
            string connectionString,
            DailyResetService dailyReset = null)
        {
            _connectionString = connectionString;
            if (dailyReset == null)
            {
                var databasePath = new SqliteConnectionStringBuilder(connectionString)
                    .DataSource;
                dailyReset = new DailyResetService(
                    databasePath,
                    ServerPaths.SchemaFilePath);
            }

            _repository = new DailyChallengeRepository(connectionString, dailyReset);
        }

        internal DailyChallengeInitializationResult EnsureInitialized(
            int characterId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));

            int characterLevel;
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT level
FROM characters
WHERE character_id = @cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    var value = command.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                        throw new InvalidOperationException(
                            $"Daily challenge character not found: {characterId}");
                    characterLevel = Convert.ToInt32(value);
                }
            }

            var plan = DailyChallengeData.BuildGenerationPlan(
                characterId,
                characterLevel,
                DailyResetService.TodayId());
            var result = _repository.EnsureInitialized(characterId, plan);
            if (result.Refreshed)
            {
                FileLogger.Log(
                    $"[DailyChallenge] generated cid={characterId} "
                    + $"level={characterLevel} groups={result.GroupCount} "
                    + $"entries={result.EntryCount}");
            }

            return result;
        }

        internal bool TryHandleSetTrigger(
            int characterId,
            byte[] body,
            out DailyChallengeSetTriggerResult result)
        {
            result = null;
            if (body == null || body.Length < 3)
                return false;

            var questId = BitConverter.ToUInt16(body, 0);
            if (!QuestData.IsDailyChallengeQuest(questId))
                return false;

            var triggerType = body[2];
            var isIncrement = body.Length >= 4 && body[3] != 0;
            var serverOwnedSuitableClear = QuestData
                .TryGetSuitableDungeonClearChallengeRule(
                    questId,
                    out _);
            var stored = _repository.ApplyMutation(
                characterId,
                questId,
                (target, current) => serverOwnedSuitableClear
                    ? current
                    : ApplyMutation(
                        target,
                        current,
                        triggerType,
                        isIncrement));

            if (!stored.Found)
            {
                var warningKey = ((long)characterId << 32) | questId;
                if (MissingEntryWarnings.TryAdd(warningKey, 0))
                {
                    FileLogger.Log(
                        $"[DailyChallenge] configured quest missing from character ledger: "
                        + $"cid={characterId} quest={questId}; returning unavailable state");
                }
            }
            else if (stored.Changed)
            {
                FileLogger.Log(
                    $"[DailyChallenge] SET_TRIGGER cid={characterId} quest={questId} "
                    + $"group={stored.GroupIndex} entry={stored.EntryIndex} "
                    + $"type=0x{triggerType:X2} inc={isIncrement} "
                    + $"remaining={stored.PreviousValue}->{stored.CurrentValue} "
                    + $"target={stored.TargetValue}");
            }
            else if (serverOwnedSuitableClear && stored.Found)
            {
                FileLogger.Log(
                    $"[DailyChallenge] SET_TRIGGER echo server-owned suitable clear "
                    + $"cid={characterId} quest={questId} "
                    + $"remaining={stored.CurrentValue} target={stored.TargetValue}");
            }

            result = new DailyChallengeSetTriggerResult(
                new QuestSetTriggerResult
                {
                    QuestId = questId,
                    PreviousTriggerValue = stored.PreviousValue,
                    TriggerValue = stored.CurrentValue,
                },
                stored.Snapshot,
                stored.Found,
                stored.Changed);
            return true;
        }

        internal DailyChallengeDungeonClearResult ApplySuitableDungeonClear(
            int characterId,
            int dungeonId,
            int difficulty,
            int characterLevel,
            Guid sourceEventId)
        {
            var result = _repository.ApplySuitableDungeonClear(
                characterId,
                dungeonId,
                difficulty,
                characterLevel,
                sourceEventId);
            if (result.ChangedEntries > 0 || result.SpecialChanged)
            {
                FileLogger.Log(
                    $"[DailyChallenge] SUITABLE_DUNGEON_CLEAR cid={characterId} "
                    + $"dungeon={dungeonId} difficulty={difficulty} "
                    + $"level={characterLevel} event={sourceEventId:N} "
                    + $"changed={result.ChangedEntries} "
                    + $"specialChanged={result.SpecialChanged} "
                    + $"specialProgress={result.Snapshot.DailyChallengeSpecialProgress}");
            }
            return result;
        }

        internal DailyChallengeResetResult ResetCharacter(int characterId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));

            var result = _repository.ResetCharacter(characterId);
            if (result.ChangedEntries > 0 || result.ClearedClaims > 0)
            {
                FileLogger.Log(
                    $"[DailyChallenge] reset cid={characterId} "
                    + $"entries={result.ChangedEntries} claims={result.ClearedClaims}");
            }

            return result;
        }

        private static uint ApplyMutation(
            uint target,
            uint storedCurrent,
            byte triggerType,
            bool isIncrement)
        {
            var current = Math.Min(target, storedCurrent);
            var next = new QuestTrigger(current)
                .ApplyClientMutation(triggerType, isIncrement)
                .PackedValue;
            return Math.Min(target, next);
        }
    }

    internal sealed class DailyChallengeSetTriggerResult
    {
        internal DailyChallengeSetTriggerResult(
            QuestSetTriggerResult ack,
            SelectCharacterInitializationSnapshot snapshot,
            bool found,
            bool changed)
        {
            Ack = ack;
            Snapshot = snapshot;
            Found = found;
            Changed = changed;
        }

        internal QuestSetTriggerResult Ack { get; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
        internal bool Found { get; }
        internal bool Changed { get; }
    }
}
