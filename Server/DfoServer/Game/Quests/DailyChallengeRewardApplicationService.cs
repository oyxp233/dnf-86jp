using System;
using System.Collections.Generic;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    // Application boundary for an atomic challenge-reward claim. The caller
    // resolves the owner, this boundary owns the inventory lease lock, and the
    // network layer projects the result only after the lock has been released.
    internal sealed class DailyChallengeRewardApplicationService
    {
        private readonly string _connectionString;
        private readonly DailyChallengeRepository _repository;

        internal DailyChallengeRewardApplicationService(string connectionString)
        {
            _connectionString = connectionString;
            var databasePath = new SqliteConnectionStringBuilder(connectionString)
                .DataSource;
            _repository = new DailyChallengeRepository(
                connectionString,
                new DailyResetService(databasePath, ServerPaths.SchemaFilePath));
        }

        internal DailyChallengeRewardClaimResult Claim(
            QuestCommandOwnerContext owner,
            int characterLevel,
            int groupIndex)
        {
            var lease = owner.InventoryLease;
            if (owner.CharacterId <= 0
                || groupIndex < 0
                || groupIndex >= 6
                || lease == null
                || lease.CharacterId != owner.CharacterId
                || lease.AccountId != owner.AccountId
                || !owner.IsCurrentInventoryOwner())
            {
                return DailyChallengeRewardClaimResult.Rejected(
                    DailyChallengeRewardClaimStatus.InvalidRequest,
                    groupIndex,
                    null);
            }

            lock (lease.SyncRoot)
            {
                if (!owner.IsCurrentInventoryOwner())
                {
                    return DailyChallengeRewardClaimResult.Rejected(
                        DailyChallengeRewardClaimStatus.InvalidRequest,
                        groupIndex,
                        null);
                }

                return ClaimUnderLeaseLock(
                    owner.CharacterId,
                    characterLevel,
                    groupIndex,
                    lease,
                    owner);
            }
        }

        private DailyChallengeRewardClaimResult ClaimUnderLeaseLock(
            int characterId,
            int characterLevel,
            int groupIndex,
            InventoryLease lease,
            QuestCommandOwnerContext owner)
        {
            SelectCharacterInitializationSnapshot snapshot = null;
            RewardInventoryRollback rollback = null;
            InventoryRewardGrantBatchResult grant = null;
            var inventoryMutated = false;

            try
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        if (!owner.IsCurrentInventoryOwner())
                        {
                            return DailyChallengeRewardClaimResult.Rejected(
                                DailyChallengeRewardClaimStatus.InvalidRequest,
                                groupIndex,
                                null);
                        }

                        var state = _repository.LoadRewardState(
                            connection,
                            transaction,
                            characterId,
                            groupIndex);
                        snapshot = DailyChallengeRepository.LoadSnapshot(
                            connection,
                            transaction,
                            characterId);

                        if (!state.Found)
                        {
                            return DailyChallengeRewardClaimResult.Rejected(
                                DailyChallengeRewardClaimStatus.GroupUnavailable,
                                groupIndex,
                                snapshot);
                        }

                        if (state.Claimed)
                        {
                            transaction.Commit();
                            return DailyChallengeRewardClaimResult.AlreadyClaimed(
                                groupIndex,
                                snapshot);
                        }

                        if (!DailyChallengeData.TryResolveReward(
                                groupIndex,
                                characterLevel,
                                state.EntryCount,
                                out var reward))
                        {
                            return DailyChallengeRewardClaimResult.Rejected(
                                DailyChallengeRewardClaimStatus.RewardUnavailable,
                                groupIndex,
                                snapshot);
                        }

                        if (state.CompletedEntryCount < reward.RequiredCompletionCount)
                        {
                            return DailyChallengeRewardClaimResult.Rejected(
                                DailyChallengeRewardClaimStatus.Incomplete,
                                groupIndex,
                                snapshot,
                                reward,
                                state.CompletedEntryCount);
                        }

                        var requests = new List<InventoryRewardGrantRequest>
                        {
                            InventoryRewardGrantRequest.Create(
                                reward.ItemId,
                                reward.ItemCount,
                                ItemCreateReason.QuestReward),
                        };
                        if (!InventoryRewardGrantService.TryPlanBatch(
                                lease.Inventory,
                                requests,
                                out var plan))
                        {
                            return DailyChallengeRewardClaimResult.Rejected(
                                DailyChallengeRewardClaimStatus.InventoryFull,
                                groupIndex,
                                snapshot,
                                reward,
                                state.CompletedEntryCount);
                        }

                        rollback = RewardInventoryRollback.Capture(
                            lease.Inventory,
                            plan.Entries[0]);
                        if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                                lease.Inventory,
                                plan,
                                out grant))
                        {
                            RewardInventoryRollback.Restore(
                                lease.Inventory,
                                rollback,
                                grant);
                            return DailyChallengeRewardClaimResult.Rejected(
                                DailyChallengeRewardClaimStatus.InventoryFull,
                                groupIndex,
                                snapshot,
                                reward,
                                state.CompletedEntryCount);
                        }

                        inventoryMutated = true;
                        if (!_repository.TryMarkRewardClaimed(
                                connection,
                                transaction,
                                characterId,
                                groupIndex))
                        {
                            RewardInventoryRollback.Restore(
                                lease.Inventory,
                                rollback,
                                grant);
                            inventoryMutated = false;
                            snapshot = DailyChallengeRepository.LoadSnapshot(
                                connection,
                                transaction,
                                characterId);
                            transaction.Commit();
                            return DailyChallengeRewardClaimResult.AlreadyClaimed(
                                groupIndex,
                                snapshot);
                        }

                        if (!owner.IsCurrentInventoryOwner())
                        {
                            throw new InvalidOperationException(
                                "daily challenge inventory lease was replaced");
                        }
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                lease))
                        {
                            throw new InvalidOperationException(
                                "daily challenge inventory persistence returned false");
                        }

                        snapshot = DailyChallengeRepository.LoadSnapshot(
                            connection,
                            transaction,
                            characterId);
                        transaction.Commit();
                        lease.Inventory.ClearDirtyState();
                        inventoryMutated = false;

                        FileLogger.Log(
                            $"[DailyChallenge] REWARD claimed cid={characterId} "
                            + $"group={groupIndex} completed={state.CompletedEntryCount}/"
                            + $"{reward.RequiredCompletionCount} item={reward.ItemId} "
                            + $"count={reward.ItemCount}");
                        return DailyChallengeRewardClaimResult.Succeeded(
                            groupIndex,
                            snapshot,
                            reward,
                            state.CompletedEntryCount,
                            grant?.Changes);
                    }
                }
            }
            catch (Exception ex)
            {
                if (inventoryMutated)
                    RewardInventoryRollback.Restore(lease.Inventory, rollback, grant);

                FileLogger.Log(
                    $"[DailyChallenge] REWARD failed cid={characterId} "
                    + $"group={groupIndex}: {ex.Message}");
                return DailyChallengeRewardClaimResult.Rejected(
                    DailyChallengeRewardClaimStatus.PersistenceFailed,
                    groupIndex,
                    snapshot);
            }
        }
    }

    internal enum DailyChallengeRewardClaimStatus
    {
        Success,
        AlreadyClaimed,
        InvalidRequest,
        GroupUnavailable,
        RewardUnavailable,
        Incomplete,
        InventoryFull,
        PersistenceFailed,
    }

    internal sealed class DailyChallengeRewardClaimResult
    {
        internal DailyChallengeRewardClaimStatus Status { get; private set; }
        internal int GroupIndex { get; private set; }
        internal int ItemId { get; private set; }
        internal int ItemCount { get; private set; }
        internal int RequiredCompletionCount { get; private set; }
        internal int CompletedEntryCount { get; private set; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; private set; }
        internal InventoryMutationSet Changes { get; private set; } = new InventoryMutationSet();
        internal bool ClientSuccess => Status == DailyChallengeRewardClaimStatus.Success
            || Status == DailyChallengeRewardClaimStatus.AlreadyClaimed;
        internal bool GrantedReward => Status == DailyChallengeRewardClaimStatus.Success;

        internal static DailyChallengeRewardClaimResult Succeeded(
            int groupIndex,
            SelectCharacterInitializationSnapshot snapshot,
            DailyChallengeRewardDefinition reward,
            int completed,
            InventoryMutationSet changes)
        {
            var result = Create(
                DailyChallengeRewardClaimStatus.Success,
                groupIndex,
                snapshot,
                reward,
                completed);
            result.Changes.AddRange(changes);
            return result;
        }

        internal static DailyChallengeRewardClaimResult AlreadyClaimed(
            int groupIndex,
            SelectCharacterInitializationSnapshot snapshot) =>
            Create(
                DailyChallengeRewardClaimStatus.AlreadyClaimed,
                groupIndex,
                snapshot,
                null,
                0);

        internal static DailyChallengeRewardClaimResult Rejected(
            DailyChallengeRewardClaimStatus status,
            int groupIndex,
            SelectCharacterInitializationSnapshot snapshot,
            DailyChallengeRewardDefinition reward = null,
            int completed = 0) =>
            Create(status, groupIndex, snapshot, reward, completed);

        private static DailyChallengeRewardClaimResult Create(
            DailyChallengeRewardClaimStatus status,
            int groupIndex,
            SelectCharacterInitializationSnapshot snapshot,
            DailyChallengeRewardDefinition reward,
            int completed) =>
            new DailyChallengeRewardClaimResult
            {
                Status = status,
                GroupIndex = groupIndex,
                ItemId = reward?.ItemId ?? 0,
                ItemCount = reward?.ItemCount ?? 0,
                RequiredCompletionCount = reward?.RequiredCompletionCount ?? 0,
                CompletedEntryCount = completed,
                Snapshot = snapshot,
            };
    }

    internal sealed class RewardInventoryRollback
    {
        internal InventoryRewardGrantKind Kind { get; private set; }
        internal InventoryListType ListType { get; private set; }
        internal short SlotIndex { get; private set; }
        internal ItemCore PreviousItem { get; private set; }
        internal VirtualCountItem PreviousVirtualCount { get; private set; }

        internal static RewardInventoryRollback Capture(
            InventoryService inventory,
            InventoryRewardGrantPlanEntry entry)
        {
            var snapshot = new RewardInventoryRollback
            {
                Kind = entry.Kind,
                ListType = entry.ListType,
                SlotIndex = entry.SlotIndex,
            };
            if (entry.Kind == InventoryRewardGrantKind.InventoryItem)
                snapshot.PreviousItem = inventory.GetItem(entry.ListType, entry.SlotIndex)?.Copy();
            else if (entry.Kind == InventoryRewardGrantKind.MainVirtualCount)
                snapshot.PreviousVirtualCount = inventory.GetMainVirtualCount(entry.SlotIndex);
            return snapshot;
        }

        internal static void Restore(
            InventoryService inventory,
            RewardInventoryRollback snapshot,
            InventoryRewardGrantBatchResult grant)
        {
            if (inventory == null || snapshot == null)
                return;

            if (grant != null)
            {
                foreach (var result in grant.Results)
                    InventoryCreateService.DetachCreatedDetails(inventory, result.CreateResult);
            }

            if (snapshot.Kind == InventoryRewardGrantKind.InventoryItem)
            {
                if (snapshot.PreviousItem == null)
                    inventory.RemoveItem(snapshot.ListType, snapshot.SlotIndex);
                else
                    inventory.SetItem(
                        snapshot.ListType,
                        snapshot.SlotIndex,
                        snapshot.PreviousItem.Copy());
            }
            else if (snapshot.Kind == InventoryRewardGrantKind.MainVirtualCount
                && snapshot.PreviousVirtualCount != null)
            {
                inventory.SetMainVirtualCount(
                    snapshot.SlotIndex,
                    snapshot.PreviousVirtualCount.ItemId,
                    snapshot.PreviousVirtualCount.Count);
            }
        }
    }
}
