using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DailyChallengeSelfTest
    {
        private const int AccountId = 986026;
        private const int CharacterId = 986126;
        private const int BootstrapCharacterId = 986127;
        private const ushort ChallengeQuestId = 14653;
        private const ushort RewardChallengeQuestId = 14732;
        private const ushort NormalQuestId = 1791;

        public static int Run()
        {
            Console.WriteLine("=== DAILY_CHALLENGE selftest ===");

            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var databasePath = Path.Combine(tempDir, "daily-challenge.db");
            DeleteDatabase(databasePath);

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            Seed(connectionString);

            var failures = 0;
            var triggerSessionId = Guid.NewGuid();
            InventoryContext.Register(
                triggerSessionId,
                new InventoryService(CharacterId, AccountId));
            Check("PVF classifies configured challenge quest",
                QuestData.IsDailyChallengeQuest(ChallengeQuestId),
                ref failures);
            Check("normal active quest is not classified as daily challenge",
                !QuestData.IsDailyChallengeQuest(NormalQuestId),
                ref failures);
            Check("challenge disjoint target comes from PVF check count",
                QuestData.GetInitTrigger(14717) == 3,
                ref failures);
            Check("challenge quest-clear target comes from compact PVF int data",
                QuestData.GetInitTrigger(14653) == 3,
                ref failures);
            Check("challenge use-skill target comes from compact PVF int data",
                QuestData.GetInitTrigger(14681) == 6,
                ref failures);
            Check("challenge repeated-clear target comes from compact PVF int data",
                QuestData.GetInitTrigger(14532) == 5,
                ref failures);
            Check("challenge clear condition threshold remains a one-clear target",
                QuestData.GetInitTrigger(14694) == 1,
                ref failures);
            Check("PVF marks recommended-level clear challenge as server-owned",
                QuestData.TryGetSuitableDungeonClearChallengeRule(
                    14532,
                    out var anyDifficulty)
                && anyDifficulty == -1,
                ref failures);
            Check("PVF preserves adventure-or-higher recommended clear rule",
                QuestData.TryGetSuitableDungeonClearChallengeRule(
                    14548,
                    out var adventureDifficulty)
                && adventureDifficulty == 1,
                ref failures);
            Check("non-repeat clear condition is not a suitable-dungeon counter",
                !QuestData.TryGetSuitableDungeonClearChallengeRule(
                    14694,
                    out _),
                ref failures);

            var generated = new DailyChallengeService(connectionString)
                .EnsureInitialized(BootstrapCharacterId);
            Check("level-61 character receives PVF groups 0 and 4",
                generated.Refreshed
                && generated.Snapshot.RacingDungeonGroups.Count == 2
                && generated.Snapshot.RacingDungeonGroups[0].GroupId == 0
                && generated.Snapshot.RacingDungeonGroups[1].GroupId == 4,
                ref failures);
            Check("PVF level table creates five regular and eight special entries",
                generated.Snapshot.RacingDungeonGroups[0].Entries.Count == 5
                && generated.Snapshot.RacingDungeonGroups[1].Entries.Count == 8
                && generated.EntryCount == 13,
                ref failures);
            Check("generated entries are challenge quests with initialized targets",
                AllGeneratedEntriesValid(generated.Snapshot),
                ref failures);
            Check("level-61 special recommended-dungeon challenge starts at zero",
                generated.Snapshot.DailyChallengeSpecialTarget == 5
                && generated.Snapshot.DailyChallengeSpecialProgress == 0,
                ref failures);

            var repeated = new DailyChallengeService(connectionString)
                .EnsureInitialized(BootstrapCharacterId);
            Check("same-day initialization preserves the existing ledger",
                !repeated.Refreshed
                && SameGeneratedEntries(generated.Snapshot, repeated.Snapshot),
                ref failures);

            MarkBootstrapLedgerCompletedAndAdvanceDay(connectionString);
            var rolled = new DailyChallengeService(connectionString)
                .EnsureInitialized(BootstrapCharacterId);
            Check("daily rollover regenerates progress and clears reward claims",
                rolled.Refreshed
                && AllGeneratedEntriesValid(rolled.Snapshot)
                && rolled.Snapshot.DailyChallengeRewardClaimFlags[4] == 0
                && rolled.Snapshot.DailyChallengeSpecialTarget == 5
                && rolled.Snapshot.DailyChallengeSpecialProgress == 0
                && !AnyEntryClaimed(connectionString, BootstrapCharacterId)
                && !HasChallengeClearedFlag(
                    connectionString,
                    BootstrapCharacterId),
                ref failures);

            var selectInit = new Game.SelectCharacter.SelectCharacterInitializationSnapshot
            {
                DailyChallengeCharacterLevel = 7,
                DailyChallengeSpecialTarget = 5,
                DailyChallengeSpecialProgress = 2,
            };
            var initialGroup = new Game.SelectCharacter.RacingDungeonGroupSnapshot
            {
                GroupId = 5,
            };
            initialGroup.Entries.Add(new Game.SelectCharacter.RacingDungeonEntrySnapshot
            {
                TrackLikeId = ChallengeQuestId,
                ValueA = 3,
                ValueB = 3,
            });
            selectInit.RacingDungeonGroups.Add(initialGroup);
            selectInit.RacingDungeonTailIds.Add(777);
            var selectSnapshot = new Game.SelectCharacter.SelectCharacterDataSnapshot
            {
                InitializationSnapshot = selectInit,
                CharacterRecord = new Game.Characters.CharacterRecord { Level = 86 },
            };
            new DailyChallengeBodyBuilder().TryBuild(selectSnapshot, 0, out var selectBody);
            Check("selection 0x0286 projects character level for special challenge lookup",
                BitConverter.ToUInt32(selectBody, 0) == 86,
                ref failures);
            Check("initial 0x0286 entry uses remaining,target wire order (3,3)",
                IsExpectedSnapshot(
                    selectBody,
                    characterLevel: 86,
                    remaining: 3,
                    expectedTailIds: new uint[] { 777, 1 }),
                ref failures);
            Check("0x0287 carries one opaque special completion token",
                BitConverter.ToString(
                    DailyChallengeClearDungeonBodyBuilder.Build(
                        2))
                    == "02-00-00-00",
                ref failures);

            var firstSender = new RecordingSender();
            var firstManager = new QuestManager(firstSender, connectionString);
            firstManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();

            Check("first challenge event persists 3 -> 2",
                ReadChallengeValue(connectionString) == 2,
                ref failures);
            Check("challenge SET_TRIGGER sends only the full 0x0286 snapshot",
                firstSender.Calls.Count == 1
                && firstSender.Calls[0] == "NOTI:0286",
                ref failures);
            Check("challenge SET_TRIGGER does not emit a 0x0021 ACK",
                firstSender.LastAckBody == null,
                ref failures);
            Check("in-progress 0x0286 entry uses remaining,target wire order (2,3)",
                IsExpectedSnapshot(
                    firstSender.LastNotiBody,
                    characterLevel: 86,
                    remaining: 2),
                ref failures);

            var rebuiltSender = new RecordingSender();
            var rebuiltManager = new QuestManager(rebuiltSender, connectionString);
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("rebuilt service reads persisted value and applies 2 -> 1",
                ReadChallengeValue(connectionString) == 1
                && rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && rebuiltSender.LastAckBody == null,
                ref failures);

            rebuiltSender.Reset();
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("third challenge event persists 1 -> 0",
                ReadChallengeValue(connectionString) == 0
                && rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && rebuiltSender.LastAckBody == null,
                ref failures);

            rebuiltSender.Reset();
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("completed 0x0286 entry uses remaining,target wire order (0,3)",
                ReadChallengeValue(connectionString) == 0
                && rebuiltSender.LastAckBody == null
                && rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && IsExpectedSnapshot(
                    rebuiltSender.LastNotiBody,
                    characterLevel: 86,
                    remaining: 0),
                ref failures);

            var resetService = new DailyChallengeService(connectionString);
            var reset = resetService.ResetCharacter(CharacterId);
            Check("daily reset restores all remaining values from their targets",
                reset.ChangedEntries == 1
                && ReadChallengeValue(connectionString) == 3
                && SnapshotValue(reset.Snapshot) == 3,
                ref failures);
            Check("repeating the daily reset is a database no-op",
                new DailyChallengeService(connectionString)
                    .ResetCharacter(CharacterId)
                    .ChangedEntries == 0,
                ref failures);

            rebuiltSender.Reset();
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(ChallengeQuestId, increment: true),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("client increment cannot exceed the persisted daily target",
                ReadChallengeValue(connectionString) == 3
                && rebuiltSender.LastAckBody == null
                && rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && IsExpectedSnapshot(
                    rebuiltSender.LastNotiBody,
                    characterLevel: 86,
                    remaining: 3),
                ref failures);

            SaveNormalActiveQuest(connectionString);
            rebuiltSender.Reset();
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(NormalQuestId, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("server-owned hunt quest only echoes a client trigger",
                ReadNormalQuestValue(connectionString) == 1
                && ReadChallengeValue(connectionString) == 3,
                ref failures);
            Check("normal quest does not emit a daily challenge snapshot",
                rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "ACK:0021",
                ref failures);

            SeedSuitableDungeonChallenges(connectionString);
            var suitableClearEvent = Guid.NewGuid();
            var suitableClear = new DailyChallengeService(connectionString)
                .ApplySuitableDungeonClear(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 2,
                    characterLevel: 62,
                    suitableClearEvent);
            Check("authoritative suitable clear advances all matching PVF rules",
                suitableClear.RelevantEntries == 2
                && suitableClear.ChangedEntries == 2
                && suitableClear.SpecialRelevant
                && suitableClear.SpecialChanged
                && suitableClear.Snapshot.DailyChallengeSpecialProgress == 1
                && ReadUInt32(connectionString,
                    "SELECT value_b FROM character_daily_challenge_entries "
                    + "WHERE character_id=@cid AND track_like_id=@id;",
                    14532) == 4
                && ReadUInt32(connectionString,
                    "SELECT value_b FROM character_daily_challenge_entries "
                    + "WHERE character_id=@cid AND track_like_id=@id;",
                    14548) == 2,
                ref failures);
            var relogSnapshot =
                new Game.SelectCharacter.SelectCharacterInitializationSnapshot();
            new Game.CharacterData.SqliteCharacterStateRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath)
                .LoadFlags(CharacterId, relogSnapshot);
            Check("relogin loader preserves persisted special challenge progress",
                relogSnapshot.DailyChallengeSpecialTarget == 5
                && relogSnapshot.DailyChallengeSpecialProgress == 1,
                ref failures);
            var replayedSuitableClear = new DailyChallengeService(connectionString)
                .ApplySuitableDungeonClear(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 2,
                    characterLevel: 62,
                    suitableClearEvent);
            Check("same authoritative clear event is idempotent",
                replayedSuitableClear.RelevantEntries == 2
                && replayedSuitableClear.ChangedEntries == 0
                && replayedSuitableClear.SpecialRelevant
                && !replayedSuitableClear.SpecialChanged
                && replayedSuitableClear.Snapshot.DailyChallengeSpecialProgress == 1
                && ReadUInt32(connectionString,
                    "SELECT value_b FROM character_daily_challenge_entries "
                    + "WHERE character_id=@cid AND track_like_id=@id;",
                    14532) == 4,
                ref failures);

            SeedSuitableDungeonChallenges(connectionString);
            rebuiltSender.Reset();
            var projectedClearEvent = Guid.NewGuid();
            rebuiltManager.SyncSuitableDungeonDailyChallengeAsync(
                    dungeonId: 84,
                    difficulty: 2,
                    characterLevel: 62,
                    projectedClearEvent)
                .GetAwaiter()
                .GetResult();
            rebuiltSender.NotiBodies.TryGetValue(
                0x0286,
                out var suitableSnapshotBody);
            var projectedTailIds = suitableSnapshotBody == null
                ? new List<uint>()
                : ReadTailIds(suitableSnapshotBody);
            Check("combined clear snapshots ordinary progress before its special token",
                rebuiltSender.Calls.Count == 2
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && rebuiltSender.Calls[1] == "NOTI:0287"
                && rebuiltSender.NotiBodies.TryGetValue(
                    0x0287,
                    out var clearDungeonBody)
                && projectedTailIds.Count == 1
                && BitConverter.ToUInt32(clearDungeonBody, 0)
                    == projectedTailIds[0],
                ref failures);
            rebuiltSender.Reset();
            rebuiltManager.SyncSuitableDungeonDailyChallengeAsync(
                    dungeonId: 84,
                    difficulty: 2,
                    characterLevel: 62,
                    projectedClearEvent)
                .GetAwaiter()
                .GetResult();
            Check("idempotent settlement replay does not emit 0x0287 twice",
                rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && !rebuiltSender.NotiBodies.ContainsKey(0x0287),
                ref failures);

            rebuiltSender.Reset();
            rebuiltManager.HandleSetTriggerAsync(
                    0x0021,
                    BuildWireSetTriggerBody(14532, increment: false),
                    triggerSessionId)
                .GetAwaiter()
                .GetResult();
            Check("client suitable-clear echo cannot double-count server progress",
                ReadUInt32(connectionString,
                    "SELECT value_b FROM character_daily_challenge_entries "
                    + "WHERE character_id=@cid AND track_like_id=@id;",
                    14532) == 4
                && rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286",
                ref failures);

            SeedOrdinaryOnlySuitableDungeonChallenge(connectionString);
            rebuiltSender.Reset();
            rebuiltManager.SyncSuitableDungeonDailyChallengeAsync(
                    dungeonId: 84,
                    difficulty: 2,
                    characterLevel: 62,
                    Guid.NewGuid())
                .GetAwaiter()
                .GetResult();
            Check("ordinary-only suitable clear emits only its committed snapshot",
                rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0286"
                && !rebuiltSender.NotiBodies.ContainsKey(0x0287)
                && ReadUInt32(connectionString,
                    "SELECT value_b FROM character_daily_challenge_entries "
                    + "WHERE character_id=@cid AND track_like_id=@id;",
                    14532) == 4,
                ref failures);

            SeedSpecialOnlySuitableDungeonChallenge(connectionString);
            rebuiltSender.Reset();
            rebuiltManager.SyncSuitableDungeonDailyChallengeAsync(
                    dungeonId: 84,
                    difficulty: 2,
                    characterLevel: 62,
                    Guid.NewGuid())
                .GetAwaiter()
                .GetResult();
            var firstSpecialToken = rebuiltSender.NotiBodies.TryGetValue(
                    0x0287,
                    out var firstSpecialBody)
                ? BitConverter.ToUInt32(firstSpecialBody, 0)
                : 0;
            Check("special-only clear emits one 0x0287 without a resetting snapshot",
                rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0287"
                && firstSpecialToken != 0
                && !rebuiltSender.NotiBodies.ContainsKey(0x0286),
                ref failures);

            rebuiltSender.Reset();
            rebuiltManager.SyncSuitableDungeonDailyChallengeAsync(
                    dungeonId: 84,
                    difficulty: 2,
                    characterLevel: 62,
                    Guid.NewGuid())
                .GetAwaiter()
                .GetResult();
            var secondSpecialToken = rebuiltSender.NotiBodies.TryGetValue(
                    0x0287,
                    out var secondSpecialBody)
                ? BitConverter.ToUInt32(secondSpecialBody, 0)
                : 0;
            var repeatedClearSnapshot =
                new Game.SelectCharacter.SelectCharacterInitializationSnapshot();
            new Game.CharacterData.SqliteCharacterStateRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath)
                .LoadFlags(CharacterId, repeatedClearSnapshot);
            var repeatedClearTailIds = ReadTailIds(
                DailyChallengeBodyBuilder.Build(repeatedClearSnapshot));
            Check("repeating the same suitable dungeon adds a distinct durable token",
                rebuiltSender.Calls.Count == 1
                && rebuiltSender.Calls[0] == "NOTI:0287"
                && secondSpecialToken != 0
                && secondSpecialToken != firstSpecialToken
                && repeatedClearSnapshot.DailyChallengeSpecialProgress == 2
                && repeatedClearTailIds.Count == 2
                && repeatedClearTailIds[0] != repeatedClearTailIds[1],
                ref failures);

            Check("PVF group index 4 resolves the configured level-86 reward",
                DailyChallengeData.TryResolveReward(4, 86, 2, out var configuredReward)
                && configuredReward.RequiredCompletionCount == 2
                && configuredReward.ItemId == 10099412
                && configuredReward.ItemCount == 1,
                ref failures);

            InventoryContext.Unregister(triggerSessionId, CharacterId);
            SeedCompletedRewardGroup(connectionString);
            var sessionId = Guid.NewGuid();
            var inventory = new InventoryService(CharacterId, AccountId);
            InventoryContext.Register(sessionId, inventory);
            try
            {
                var rewardManager = new QuestManager(rebuiltSender, connectionString);
                var firstClaim = rewardManager.HandleDailyChallengeReward(
                    sessionId,
                    BitConverter.GetBytes(4));
                Check("first reward claim grants one configured item",
                    firstClaim.Status == DailyChallengeRewardClaimStatus.Success
                    && inventory.CountMainItem(configuredReward.ItemId) == configuredReward.ItemCount,
                    ref failures);
                Check("reward claim persists group flag 4",
                    ReadClaimed(connectionString, 4)
                    && firstClaim.Snapshot.DailyChallengeRewardClaimFlags[4] == 1,
                    ref failures);
                Check("reward success ACK matches A14 handler layout",
                    BitConverter.ToString(DailyChallengeRewardAckBuilder.Build(firstClaim))
                        == "01-04-00-00-00-00-00-00-00",
                    ref failures);
                Check("0x0286 projects persisted claimed flag 4",
                    ReadClaimFlags(DailyChallengeBodyBuilder.Build(firstClaim.Snapshot))[4] == 1,
                    ref failures);

                var replay = rewardManager.HandleDailyChallengeReward(
                    sessionId,
                    BitConverter.GetBytes(4));
                Check("replayed reward claim is idempotent success",
                    replay.Status == DailyChallengeRewardClaimStatus.AlreadyClaimed
                    && replay.ClientSuccess
                    && inventory.CountMainItem(configuredReward.ItemId) == configuredReward.ItemCount,
                    ref failures);

                var rebuiltRewardManager = new QuestManager(rebuiltSender, connectionString);
                var relogReplay = rebuiltRewardManager.HandleDailyChallengeReward(
                    sessionId,
                    BitConverter.GetBytes(4));
                Check("rebuilt service retains claimed state",
                    relogReplay.Status == DailyChallengeRewardClaimStatus.AlreadyClaimed
                    && relogReplay.Snapshot.DailyChallengeRewardClaimFlags[4] == 1,
                    ref failures);

                var rewardReset = new DailyChallengeService(connectionString)
                    .ResetCharacter(CharacterId);
                Check("daily reset clears reward claims",
                    rewardReset.ClearedClaims == 1
                    && !ReadClaimed(connectionString, 4)
                    && rewardReset.Snapshot.DailyChallengeRewardClaimFlags[4] == 0,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
            }

            SeedCompletedEntryReward(connectionString);
            var entryRewardSessionId = Guid.NewGuid();
            var entryRewardInventory = new InventoryService(CharacterId, AccountId);
            if (!InventoryRewardGrantService.TryGrant(
                    entryRewardInventory,
                    InventoryRewardGrantRequest.Create(
                        3309,
                        9,
                        ItemCreateReason.AdminGrant),
                    out var requirementGrant)
                || !requirementGrant.Success)
            {
                throw new InvalidOperationException(
                    "daily challenge entry requirement fixture failed");
            }
            entryRewardInventory.ClearDirtyState();
            InventoryContext.Register(entryRewardSessionId, entryRewardInventory);
            try
            {
                rebuiltSender.Reset();
                var entryRewardManager = new QuestManager(
                    rebuiltSender,
                    connectionString);
                entryRewardManager.HandleFinishQuestAsync(
                        0x0022,
                        BuildWireFinishBody(RewardChallengeQuestId),
                        entryRewardSessionId)
                    .GetAwaiter()
                    .GetResult();
                Check("completed challenge entry reuses normal QST reward transaction",
                    entryRewardInventory.CountMainItem(3309) == 0
                    && entryRewardInventory.CountMainItem(3300) == 4,
                    ref failures);
                Check("challenge entry reward persists a dedicated idempotency claim",
                    ReadEntryClaimed(connectionString, RewardChallengeQuestId),
                    ref failures);
                Check("challenge entry reward emits a normal FINISH_QUEST success ACK",
                    rebuiltSender.LastAckBody?.Length > 3
                    && rebuiltSender.LastAckBody[0] == 1
                    && BitConverter.ToUInt16(
                        rebuiltSender.LastAckBody,
                        1) == RewardChallengeQuestId,
                    ref failures);
                Check("challenge entry claim refreshes the durable clear-list projection",
                    rebuiltSender.Calls.IndexOf("ACK:0022") >= 0
                    && rebuiltSender.Calls.IndexOf("NOTI:0164")
                        > rebuiltSender.Calls.IndexOf("ACK:0022")
                    && rebuiltSender.NotiBodies.TryGetValue(
                        0x0164,
                        out var clearListBody)
                    && clearListBody.Length
                        == 4 + ClearQuestListBodyBuilder.PayloadLength
                    && clearListBody[4 + RewardChallengeQuestId] == 1,
                    ref failures);

                rebuiltSender.Reset();
                entryRewardManager.HandleFinishQuestAsync(
                        0x0022,
                        BuildWireFinishBody(RewardChallengeQuestId),
                        entryRewardSessionId)
                    .GetAwaiter()
                    .GetResult();
                Check("replayed challenge entry reward cannot duplicate items",
                    BitConverter.ToString(rebuiltSender.LastAckBody) == "00-16"
                    && entryRewardInventory.CountMainItem(3300) == 4,
                    ref failures);
                Check("challenge reward projects a relog flag without an active quest",
                    !HasActiveQuest(
                        connectionString,
                        RewardChallengeQuestId)
                    && HasClearedFlag(
                        connectionString,
                        CharacterId,
                        RewardChallengeQuestId),
                    ref failures);

                var entryReset = new DailyChallengeService(connectionString)
                    .ResetCharacter(CharacterId);
                Check("daily reset clears entry claims and restores its target",
                    entryReset.ChangedEntries == 1
                    && entryReset.ClearedClaims == 1
                    && !ReadEntryClaimed(
                        connectionString,
                        RewardChallengeQuestId)
                    && !HasClearedFlag(
                        connectionString,
                        CharacterId,
                        RewardChallengeQuestId)
                    && ReadUInt32(
                        connectionString,
                        "SELECT value_b FROM character_daily_challenge_entries "
                        + "WHERE character_id=@cid AND track_like_id=@id;",
                        RewardChallengeQuestId) == 1,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(entryRewardSessionId, CharacterId);
            }

            SeedCompletedRewardGroup(connectionString);
            var fullSessionId = Guid.NewGuid();
            var fullInventory = BuildFullInventory();
            InventoryContext.Register(fullSessionId, fullInventory);
            try
            {
                var fullClaim = new QuestManager(rebuiltSender, connectionString)
                    .HandleDailyChallengeReward(fullSessionId, BitConverter.GetBytes(4));
                Check("full inventory rejects reward without claiming it",
                    fullClaim.Status == DailyChallengeRewardClaimStatus.InventoryFull
                    && !ReadClaimed(connectionString, 4)
                    && fullClaim.Snapshot.DailyChallengeRewardClaimFlags[4] == 0,
                    ref failures);
                Check("reward failure ACK uses the minimal A14 failure layout",
                    BitConverter.ToString(DailyChallengeRewardAckBuilder.Build(fullClaim)) == "00-00",
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(fullSessionId, CharacterId);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildWireSetTriggerBody(ushort questId, bool increment)
        {
            var body = new byte[6];
            BitConverter.GetBytes((ushort)0x0021).CopyTo(body, 0);
            BitConverter.GetBytes(questId).CopyTo(body, 2);
            body[4] = 0;
            body[5] = increment ? (byte)1 : (byte)0;
            return body;
        }

        private static byte[] BuildWireFinishBody(ushort questId)
        {
            var body = new byte[10];
            BitConverter.GetBytes((ushort)0x0022).CopyTo(body, 0);
            BitConverter.GetBytes(questId).CopyTo(body, 2);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 4);
            BitConverter.GetBytes((ushort)1).CopyTo(body, 6);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 8);
            return body;
        }

        private static bool AllGeneratedEntriesValid(
            Game.SelectCharacter.SelectCharacterInitializationSnapshot snapshot)
        {
            foreach (var group in snapshot.RacingDungeonGroups)
            {
                foreach (var entry in group.Entries)
                {
                    if (!QuestData.IsDailyChallengeQuest((int)entry.TrackLikeId)
                        || entry.ValueA == 0
                        || entry.ValueB != entry.ValueA)
                    {
                        return false;
                    }
                }
            }

            return snapshot.RacingDungeonGroups.Count > 0;
        }

        private static bool SameGeneratedEntries(
            Game.SelectCharacter.SelectCharacterInitializationSnapshot left,
            Game.SelectCharacter.SelectCharacterInitializationSnapshot right)
        {
            if (left.RacingDungeonGroups.Count != right.RacingDungeonGroups.Count)
                return false;

            for (var groupIndex = 0;
                groupIndex < left.RacingDungeonGroups.Count;
                groupIndex++)
            {
                var leftGroup = left.RacingDungeonGroups[groupIndex];
                var rightGroup = right.RacingDungeonGroups[groupIndex];
                if (leftGroup.GroupId != rightGroup.GroupId
                    || leftGroup.Entries.Count != rightGroup.Entries.Count)
                {
                    return false;
                }

                for (var entryIndex = 0;
                    entryIndex < leftGroup.Entries.Count;
                    entryIndex++)
                {
                    if (leftGroup.Entries[entryIndex].TrackLikeId
                        != rightGroup.Entries[entryIndex].TrackLikeId)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void MarkBootstrapLedgerCompletedAndAdvanceDay(
            string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_daily_challenge_entries
SET value_b = 0
WHERE character_id = @cid;
UPDATE character_daily_challenge_special_state
SET progress_value = 3
WHERE character_id = @cid;
INSERT OR IGNORE INTO character_daily_challenge_special_progress_events
    (character_id, source_event_id)
VALUES (@cid, 'daily-rollover-selftest');
INSERT OR IGNORE INTO character_daily_challenge_entry_claims
    (character_id, group_index, entry_index, quest_id)
SELECT character_id, group_index, entry_index, track_like_id
FROM character_daily_challenge_entries
WHERE character_id = @cid
ORDER BY group_index, entry_index
LIMIT 1;
INSERT OR REPLACE INTO character_invisible_falgs
    (character_id, slot_index, flag_value)
SELECT character_id, track_like_id, 1
FROM character_daily_challenge_entries
WHERE character_id = @cid
ORDER BY group_index, entry_index
LIMIT 1;
INSERT OR IGNORE INTO character_daily_challenge_claims
    (character_id, group_index)
VALUES (@cid, 4);
UPDATE character_daily_reset
SET day_id = day_id - 1
WHERE character_id = @cid;";
                    command.Parameters.AddWithValue("@cid", BootstrapCharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static bool IsExpectedSnapshot(
            byte[] body,
            uint characterLevel,
            uint remaining,
            IReadOnlyList<uint> expectedTailIds = null)
        {
            expectedTailIds ??= new uint[] { 777 };
            if (body == null || body.Length != 42 + expectedTailIds.Count * 4)
                return false;

            if (BitConverter.ToUInt32(body, 0) != characterLevel
                || BitConverter.ToUInt32(body, 4) != 1
                || BitConverter.ToUInt32(body, 8) != 5
                || BitConverter.ToUInt32(body, 12) != 1
                || BitConverter.ToUInt32(body, 16) != ChallengeQuestId
                || BitConverter.ToUInt32(body, 20) != remaining
                || BitConverter.ToUInt32(body, 24) != 3
                || BitConverter.ToUInt32(body, 28) != 6
                || BitConverter.ToUInt32(body, 38) != expectedTailIds.Count)
            {
                return false;
            }

            for (var index = 0; index < expectedTailIds.Count; index++)
            {
                if (BitConverter.ToUInt32(body, 42 + index * 4)
                    != expectedTailIds[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static List<uint> ReadTailIds(byte[] body)
        {
            var offset = 4;
            var groupCount = checked((int)BitConverter.ToUInt32(body, offset));
            offset += 4;
            for (var group = 0; group < groupCount; group++)
            {
                offset += 4;
                var entryCount = checked((int)BitConverter.ToUInt32(body, offset));
                offset += 4 + entryCount * 12;
            }

            var flagCount = checked((int)BitConverter.ToUInt32(body, offset));
            offset += 4 + flagCount;
            var tailCount = checked((int)BitConverter.ToUInt32(body, offset));
            offset += 4;
            var result = new List<uint>(tailCount);
            for (var index = 0; index < tailCount; index++)
            {
                result.Add(BitConverter.ToUInt32(body, offset));
                offset += 4;
            }
            return result;
        }

        private static uint SnapshotValue(Game.SelectCharacter.SelectCharacterInitializationSnapshot snapshot)
        {
            if (snapshot?.RacingDungeonGroups.Count != 1
                || snapshot.RacingDungeonGroups[0].Entries.Count != 1)
            {
                return uint.MaxValue;
            }

            return snapshot.RacingDungeonGroups[0].Entries[0].ValueB;
        }

        private static byte[] ReadClaimFlags(byte[] body)
        {
            var offset = 4;
            var groupCount = checked((int)BitConverter.ToUInt32(body, offset));
            offset += 4;
            for (var group = 0; group < groupCount; group++)
            {
                offset += 4;
                var entryCount = checked((int)BitConverter.ToUInt32(body, offset));
                offset += 4 + entryCount * 12;
            }

            var flagCount = checked((int)BitConverter.ToUInt32(body, offset));
            offset += 4;
            var flags = new byte[flagCount];
            Buffer.BlockCopy(body, offset, flags, 0, flagCount);
            return flags;
        }

        private static InventoryService BuildFullInventory()
        {
            var inventory = new InventoryService(CharacterId, AccountId);
            if (!InventoryRewardGrantService.TryCreateOnly(
                    10099407,
                    ItemCreateReason.AdminGrant,
                    1,
                    out var created)
                || created?.Core == null)
            {
                throw new InvalidOperationException("daily challenge full-inventory fixture item failed");
            }

            for (short slot = InventoryService.MainSlotStart;
                slot <= InventoryService.MainSlotEnd;
                slot++)
            {
                inventory.AttachItem(InventoryListType.Main, slot, created.Core.Copy());
            }

            inventory.ClearDirtyState();
            return inventory;
        }

        private static void SeedCompletedRewardGroup(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO character_daily_challenge_groups (character_id, group_index, group_id)
VALUES (@cid, 4, 4);
INSERT INTO character_daily_challenge_entries
    (character_id, group_index, entry_index, track_like_id, value_a, value_b)
VALUES (@cid, 4, 0, 14734, 1, 0),
       (@cid, 4, 1, 14738, 1, 0);";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void CompleteRewardGroup(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_daily_challenge_entries
SET value_b = 0
WHERE character_id = @cid AND group_index = 4;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SeedCompletedEntryReward(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
DELETE FROM character_daily_challenge_entry_claims
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_claims
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_entries
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_groups
WHERE character_id = @cid;
INSERT INTO character_daily_challenge_groups
    (character_id, group_index, group_id)
VALUES (@cid, 0, 0);
INSERT INTO character_daily_challenge_entries
    (character_id, group_index, entry_index, track_like_id, value_a, value_b)
VALUES (@cid, 0, 0, @questId, 1, 0);";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue(
                        "@questId",
                        RewardChallengeQuestId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SeedSuitableDungeonChallenges(
            string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
DELETE FROM character_daily_challenge_progress_events
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_special_progress_events
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_special_state
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_entry_claims
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_entries
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_groups
WHERE character_id = @cid;
INSERT INTO character_daily_challenge_groups
    (character_id, group_index, group_id)
VALUES (@cid, 0, 0);
INSERT INTO character_daily_challenge_entries
    (character_id, group_index, entry_index, track_like_id, value_a, value_b)
VALUES (@cid, 0, 0, 14532, 5, 5),
       (@cid, 0, 1, 14548, 3, 3);";
                    command.CommandText += @"
INSERT INTO character_daily_challenge_special_state
    (character_id, challenge_type, target_value, progress_value)
VALUES (@cid, 12, 5, 0);";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SeedOrdinaryOnlySuitableDungeonChallenge(
            string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
DELETE FROM character_daily_challenge_progress_events
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_special_progress_events
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_special_state
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_entries
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_groups
WHERE character_id = @cid;
INSERT INTO character_daily_challenge_groups
    (character_id, group_index, group_id)
VALUES (@cid, 0, 0);
INSERT INTO character_daily_challenge_entries
    (character_id, group_index, entry_index, track_like_id, value_a, value_b)
VALUES (@cid, 0, 0, 14532, 5, 5);";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SeedSpecialOnlySuitableDungeonChallenge(
            string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
DELETE FROM character_daily_challenge_progress_events
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_special_progress_events
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_special_state
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_entries
WHERE character_id = @cid;
DELETE FROM character_daily_challenge_groups
WHERE character_id = @cid;
INSERT INTO character_daily_challenge_special_state
    (character_id, challenge_type, target_value, progress_value)
VALUES (@cid, 12, 5, 0);";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static bool ReadClaimed(string connectionString, int groupIndex)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT 1
FROM character_daily_challenge_claims
WHERE character_id = @cid AND group_index = @groupIndex;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@groupIndex", groupIndex);
                    return command.ExecuteScalar() != null;
                }
            }
        }

        private static bool ReadEntryClaimed(
            string connectionString,
            ushort questId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT 1
FROM character_daily_challenge_entry_claims
WHERE character_id = @cid AND quest_id = @questId;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@questId", (int)questId);
                    return command.ExecuteScalar() != null;
                }
            }
        }

        private static bool HasActiveQuest(
            string connectionString,
            ushort questId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM character_active_quests
WHERE character_id = @cid AND quest_id = @questId;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@questId", (int)questId);
                    return Convert.ToInt32(command.ExecuteScalar()) != 0;
                }
            }
        }

        private static bool HasClearedFlag(
            string connectionString,
            int characterId,
            ushort questId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT 1
FROM character_invisible_falgs
WHERE character_id = @cid AND slot_index = @questId;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@questId", (int)questId);
                    return command.ExecuteScalar() != null;
                }
            }
        }

        private static bool AnyEntryClaimed(
            string connectionString,
            int characterId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT 1
FROM character_daily_challenge_entry_claims
WHERE character_id = @cid
LIMIT 1;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return command.ExecuteScalar() != null;
                }
            }
        }

        private static bool HasChallengeClearedFlag(
            string connectionString,
            int characterId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT 1
FROM character_invisible_falgs AS f
WHERE f.character_id = @cid
  AND f.slot_index BETWEEN 14000 AND 15000
LIMIT 1;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return command.ExecuteScalar() != null;
                }
            }
        }

        private static void Seed(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'daily-challenge-selftest', '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES (@cid, @aid, 'daily-challenge-selftest', 86);
INSERT INTO characters (character_id, account_id, name, level)
VALUES (@bootstrapCid, @aid, 'daily-challenge-bootstrap', 61);
INSERT INTO character_init_flags (character_id, racing_dungeon_current_enter_count)
VALUES (@cid, 7);
INSERT INTO character_init_flags (character_id, racing_dungeon_current_enter_count)
VALUES (@bootstrapCid, 0);
INSERT INTO character_daily_challenge_groups (character_id, group_index, group_id)
VALUES (@cid, 0, 5);
INSERT INTO character_daily_challenge_entries
    (character_id, group_index, entry_index, track_like_id, value_a, value_b)
VALUES (@cid, 0, 0, @questId, 3, 3);
INSERT INTO character_daily_challenge_tail_ids (character_id, sort_order, id_value)
VALUES (@cid, 0, 777);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@bootstrapCid", BootstrapCharacterId);
                    command.Parameters.AddWithValue("@questId", ChallengeQuestId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SaveNormalActiveQuest(string connectionString)
        {
            QuestService.SaveActiveQuests(
                connectionString,
                CharacterId,
                new List<ActiveQuest>
                {
                    new ActiveQuest
                    {
                        Slot = 0,
                        QuestId = NormalQuestId,
                        TriggerValue = 1,
                    },
                });
        }

        private static uint ReadChallengeValue(string connectionString)
        {
            return ReadUInt32(
                connectionString,
                "SELECT value_b FROM character_daily_challenge_entries "
                + "WHERE character_id=@cid AND track_like_id=@id;",
                ChallengeQuestId);
        }

        private static uint ReadNormalQuestValue(string connectionString)
        {
            return ReadUInt32(
                connectionString,
                "SELECT trigger_value FROM character_active_quests "
                + "WHERE character_id=@cid AND quest_id=@id;",
                NormalQuestId);
        }

        private static uint ReadUInt32(
            string connectionString,
            string sql,
            ushort id)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@id", id);
                    var value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value
                        ? uint.MaxValue
                        : (uint)Convert.ToInt64(value);
                }
            }
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class RecordingSender : ISessionPacketSender
        {
            internal List<string> Calls { get; } = new List<string>();
            internal Dictionary<ushort, byte[]> NotiBodies { get; } =
                new Dictionary<ushort, byte[]>();
            internal byte[] LastAckBody { get; private set; }
            internal byte[] LastNotiBody { get; private set; }

            public PlayerContext Player { get; } = new PlayerContext
            {
                CharacterId = DailyChallengeSelfTest.CharacterId,
                Level = 86,
            };

            public int CharacterId => DailyChallengeSelfTest.CharacterId;
            public int AccountId => DailyChallengeSelfTest.AccountId;

            public Task SendPacketAsync(byte[] rawPacket) => Task.CompletedTask;

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                Calls.Add($"NOTI:{notiType:X4}");
                LastNotiBody = body;
                NotiBodies[notiType] = body;
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                Calls.Add($"ACK:{cmdType:X4}");
                LastAckBody = body;
                return Task.CompletedTask;
            }

            internal void Reset()
            {
                Calls.Clear();
                NotiBodies.Clear();
                LastAckBody = null;
                LastNotiBody = null;
            }
        }
    }
}
