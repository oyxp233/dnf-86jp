using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Inventory
{
    internal sealed class ExperienceItemUseService
    {
        private readonly string _connectionString;
        private readonly IRentalTimeProvider _timeProvider;
        private readonly ExperienceItemCooldownTracker _cooldowns;
        private readonly SqliteCharacterProgressRepository _progressRepository;

        internal ExperienceItemUseService(
            string databasePath,
            string schemaFilePath,
            IRentalTimeProvider timeProvider,
            ExperienceItemCooldownTracker cooldowns)
        {
            if (databasePath == null) throw new ArgumentNullException(nameof(databasePath));
            if (schemaFilePath == null) throw new ArgumentNullException(nameof(schemaFilePath));

            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
            _timeProvider = timeProvider
                ?? throw new ArgumentNullException(nameof(timeProvider));
            _cooldowns = cooldowns
                ?? throw new ArgumentNullException(nameof(cooldowns));
            _progressRepository = SqliteCharacterProgressRepository.FromConnectionString(_connectionString);
        }

        internal ExperienceItemUseResult UseBySlot(
            int characterId,
            int accountId,
            InventoryListType listType,
            short slotIndex,
            ExperienceItemUseLocation location)
        {
            if (listType != InventoryListType.Main || characterId <= 0 || slotIndex < 0)
                return Reject(ExperienceItemUseStatus.NotApplicable, 0, "invalid source slot");

            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || lease.Inventory == null)
                return Reject(ExperienceItemUseStatus.NotApplicable, 0, "online inventory is unavailable");

            if (accountId <= 0 || lease.AccountId != accountId)
                return Reject(ExperienceItemUseStatus.InvalidOwner, 0, "inventory lease/account ownership mismatch");

            var resolvedItemId = 0;
            var sourceConsumed = false;
            ItemCore sourceSnapshot = null;
            InventoryService inventory = null;
            ExperienceItemCooldownReservation cooldownReservation = null;
            try
            {
                lock (lease.SyncRoot)
                {
                    inventory = lease.Inventory;
                    var source = inventory.GetItem(listType, slotIndex);
                    if (source == null || source.IsEmpty)
                        return Reject(ExperienceItemUseStatus.NotApplicable, 0, "source slot is empty");

                    sourceSnapshot = source.Copy();
                    resolvedItemId = sourceSnapshot.ItemId;

                    // 道具42(复活币礼盒): 消耗1个礼盒 → 复活币+1
                    if (resolvedItemId == ReviveCoinService.ConsumableItemId)
                    {
                        if (!InventoryDeleteService.TryConsumeFromSlot(
                                inventory,
                                listType,
                                slotIndex,
                                resolvedItemId,
                                1,
                                out var deleteResult)
                            || !deleteResult.Success
                            || deleteResult.DeletedCount != 1)
                        {
                            return Reject(
                                ExperienceItemUseStatus.ConsumeFailed,
                                resolvedItemId,
                                "inventory deduction failed");
                        }

                        var current = inventory.CountMainItem(ReviveCoinService.ItemId);
                        if (!inventory.SetMainVirtualCount(
                                ReviveCoinService.WalletSlot,
                                ReviveCoinService.ItemId,
                                current + 1))
                        {
                            RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                            return Reject(
                                ExperienceItemUseStatus.PersistenceFailed,
                                resolvedItemId,
                                "failed to grant revive coin");
                        }

                        InventoryPersistenceService.SaveDirty(lease);
                        inventory.ClearDirtyState();

                        return new ExperienceItemUseResult
                        {
                            Status = ExperienceItemUseStatus.Success,
                            AccountId = accountId,
                            ItemTemplateId = resolvedItemId,
                            ConsumedItem = BuildConsumedMutation(
                                listType, slotIndex, sourceSnapshot, deleteResult),
                        };
                    }

                    var definition = ExperienceItemDataProvider.Resolve(resolvedItemId);
                    var skillPointBook = SkillPointBookDataProvider.Resolve(resolvedItemId);
                    if (!definition.IsExperienceLike && !skillPointBook.IsSkillPointBook)
                    {
                        return Reject(
                            ExperienceItemUseStatus.UnsupportedDefinition,
                            resolvedItemId,
                            "source item is neither ordinary character experience nor a skill-point book");
                    }
                    if (skillPointBook.IsSkillPointBook && !skillPointBook.IsSupported)
                    {
                        return Reject(
                            ExperienceItemUseStatus.UnsupportedDefinition,
                            resolvedItemId,
                            skillPointBook.UnsupportedReason
                                ?? "skill-point book definition is unsupported");
                    }

                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            var currentSource = inventory.GetItem(listType, slotIndex);
                            if (currentSource == null || currentSource.ItemId != resolvedItemId)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.NotApplicable,
                                    resolvedItemId,
                                    "source slot changed during use");
                            }

                            if (currentSource.Count <= 0)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.ConsumeFailed,
                                    resolvedItemId,
                                    "source stack is empty");
                            }

                            var character = _progressRepository.LoadProgressSnapshot(
                                connection,
                                transaction,
                                characterId);
                            if (character == null
                                || accountId <= 0
                                || character.AccountId != accountId)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.InvalidOwner,
                                    resolvedItemId,
                                    "character/account ownership mismatch");
                            }

                            if (skillPointBook.IsSupported)
                            {
                                var skillBookPlan = ValidateSkillPointBookUse(
                                    skillPointBook,
                                    currentSource,
                                    character,
                                    _timeProvider.UtcNowUnixSeconds());
                                if (!skillBookPlan.Success)
                                {
                                    return Reject(
                                        skillBookPlan.Status,
                                        resolvedItemId,
                                        skillBookPlan.Detail);
                                }

                                var newBonusSp = (long)character.BonusSp + skillPointBook.GrantedSp;
                                var newBonusTp = (long)character.BonusTp + skillPointBook.GrantedTp;
                                if (newBonusSp > int.MaxValue || newBonusTp > int.MaxValue)
                                {
                                    return Reject(
                                        ExperienceItemUseStatus.LevelRestricted,
                                        resolvedItemId,
                                        "skill-point bonus exceeds the database limit");
                                }

                                Characters.CharacterStatComputer.DecodeGrowType(
                                    character.GrowType,
                                    out var skillFirstGrow,
                                    out var skillSecondGrow);
                                var syncedSkillBookState = SkillStateService.LoadAndSync(
                                    _progressRepository,
                                    connection,
                                    transaction,
                                    characterId,
                                    character.Job,
                                    character.Level,
                                    (int)newBonusSp,
                                    (int)newBonusTp,
                                    persist: false,
                                    growType: skillFirstGrow,
                                    secondGrowType: skillSecondGrow);
                                if (syncedSkillBookState.Points == null)
                                {
                                    return Reject(
                                        ExperienceItemUseStatus.PersistenceFailed,
                                        resolvedItemId,
                                        "skill-point synchronization failed");
                                }

                                // 客户端技能点字段是 UInt16，先校验再消耗道具，避免溢出后丢书。
                                var points = syncedSkillBookState.Points;
                                if (points.TotalSp > ushort.MaxValue
                                    || points.RemainingSp > ushort.MaxValue
                                    || points.RemainingSpPage1 > ushort.MaxValue
                                    || points.TotalTp > ushort.MaxValue
                                    || points.RemainingTp > ushort.MaxValue
                                    || points.RemainingTpPage1 > ushort.MaxValue)
                                {
                                    return Reject(
                                        ExperienceItemUseStatus.LevelRestricted,
                                        resolvedItemId,
                                        "skill-point total exceeds the client protocol limit");
                                }

                                if (!InventoryDeleteService.TryConsumeFromSlot(
                                        inventory,
                                        listType,
                                        slotIndex,
                                        resolvedItemId,
                                        1,
                                        out var skillBookDelete)
                                    || !skillBookDelete.Success
                                    || skillBookDelete.DeletedCount != 1)
                                {
                                    return Reject(
                                        ExperienceItemUseStatus.ConsumeFailed,
                                        resolvedItemId,
                                        "inventory deduction failed");
                                }

                                sourceConsumed = true;
                                var consumedSkillBook = BuildConsumedMutation(
                                    listType,
                                    slotIndex,
                                    sourceSnapshot,
                                    skillBookDelete);

                                // 技能点与背包扣除使用同一事务提交，任何一步失败都会恢复源道具。
                                using (var update = connection.CreateCommand())
                                {
                                    update.Transaction = transaction;
                                    update.CommandText = @"
UPDATE characters
SET bonus_sp=@newBonusSp,
    bonus_tp=@newBonusTp,
    updated_at=CURRENT_TIMESTAMP
WHERE character_id=@cid
  AND account_id=@aid
  AND delete_flag=0
  AND bonus_sp=@oldBonusSp
  AND bonus_tp=@oldBonusTp;";
                                    update.Parameters.AddWithValue("@newBonusSp", (int)newBonusSp);
                                    update.Parameters.AddWithValue("@newBonusTp", (int)newBonusTp);
                                    update.Parameters.AddWithValue("@cid", characterId);
                                    update.Parameters.AddWithValue("@aid", accountId);
                                    update.Parameters.AddWithValue("@oldBonusSp", character.BonusSp);
                                    update.Parameters.AddWithValue("@oldBonusTp", character.BonusTp);
                                    if (update.ExecuteNonQuery() != 1)
                                    {
                                        RestoreConsumedSource(
                                            inventory,
                                            listType,
                                            slotIndex,
                                            sourceSnapshot);
                                        sourceConsumed = false;
                                        return Reject(
                                            ExperienceItemUseStatus.PersistenceFailed,
                                            resolvedItemId,
                                            "skill-point persistence was rejected by a concurrent update");
                                    }
                                }

                                if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                        connection,
                                        transaction,
                                        lease))
                                {
                                    RestoreConsumedSource(
                                        inventory,
                                        listType,
                                        slotIndex,
                                        sourceSnapshot);
                                    sourceConsumed = false;
                                    return Reject(
                                        ExperienceItemUseStatus.PersistenceFailed,
                                        resolvedItemId,
                                        "inventory persistence failed");
                                }

                                var skillBookGrowthExp = character.Level >= ExpTableProvider.MaxLevel
                                    ? GrowthCapsuleProgressRepository.LoadTotalExp(
                                        connection,
                                        transaction,
                                        accountId)
                                    : 0;
                                var skillBookResult = new ExperienceItemUseResult
                                {
                                    Status = ExperienceItemUseStatus.Success,
                                    AccountId = accountId,
                                    ItemTemplateId = resolvedItemId,
                                    IsSkillPointBook = true,
                                    ConsumedItem = consumedSkillBook,
                                    PreviousLevel = character.Level,
                                    NewLevel = character.Level,
                                    PreviousExp = character.Exp,
                                    NewExp = character.Exp,
                                    GrantedSp = skillPointBook.GrantedSp,
                                    GrantedTp = skillPointBook.GrantedTp,
                                    TotalGrowthCapsuleExp = skillBookGrowthExp,
                                    SyncedSkills = syncedSkillBookState.Skills,
                                    SkillPoints = SkillStateService.GetProtocolState(
                                        syncedSkillBookState.Skills,
                                        syncedSkillBookState.Points),
                                };

                                transaction.Commit();
                                inventory.ClearDirtyState();
                                sourceConsumed = false;
                                return skillBookResult;
                            }

                            var usePlan = ExperienceItemUsePolicy.Evaluate(
                                new ExperienceItemUseContext
                                {
                                    Definition = definition,
                                    SourceExpireTime = currentSource.ExpireTime,
                                    NowUnixTime = _timeProvider.UtcNowUnixSeconds(),
                                    Job = character.Job,
                                    Level = character.Level,
                                    Exp = character.Exp,
                                    IsHardcore = character.IsHardcore,
                                    Location = location,
                                });
                            if (!usePlan.Success)
                            {
                                return Reject(
                                    usePlan.Status,
                                    resolvedItemId,
                                    usePlan.Detail);
                            }

                            if (!_cooldowns.TryReserve(
                                    characterId,
                                    definition,
                                    out cooldownReservation,
                                    out var remainingCooldown))
                            {
                                return Reject(
                                    ExperienceItemUseStatus.CooldownActive,
                                    resolvedItemId,
                                    $"cooldown remaining={remainingCooldown}ms");
                            }

                            if (!InventoryDeleteService.TryConsumeFromSlot(
                                    inventory,
                                    listType,
                                    slotIndex,
                                    resolvedItemId,
                                    1,
                                    out var deleteResult)
                                || !deleteResult.Success
                                || deleteResult.DeletedCount != 1)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.ConsumeFailed,
                                    resolvedItemId,
                                    "inventory deduction failed");
                            }

                            sourceConsumed = true;
                            var consumedItem = BuildConsumedMutation(
                                listType,
                                slotIndex,
                                sourceSnapshot,
                                deleteResult);

                            var grant = Progression.CharacterExperienceService.GrantInTransaction(
                                connection,
                                transaction,
                                characterId,
                                accountId,
                                character.Level,
                                character.Exp,
                                usePlan.GrantedExp);
                            if (!grant.Persisted)
                            {
                                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "level/experience persistence failed");
                            }

                            Characters.CharacterStatComputer.DecodeGrowType(character.GrowType, out var expFirstGrow, out var expSecondGrow);
                            var syncedSkills = SkillStateService.LoadAndSync(
                                _progressRepository,
                                connection,
                                transaction,
                                characterId,
                                character.Job,
                                grant.NewLevel,
                                character.BonusSp,
                                character.BonusTp,
                                persist: grant.LeveledUp,
                                growType: expFirstGrow,
                                secondGrowType: expSecondGrow);
                            if (syncedSkills.Points == null)
                            {
                                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "skill-point synchronization failed");
                            }

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "inventory persistence failed");
                            }

                            var totalGrowthCapsuleExp = grant.TotalGrowthCapsuleExp;
                            if (grant.HonorExpGain == 0 && grant.NewLevel >= ExpTableProvider.MaxLevel)
                            {
                                totalGrowthCapsuleExp = GrowthCapsuleProgressRepository.LoadTotalExp(
                                    connection,
                                    transaction,
                                    accountId);
                            }

                            var result = new ExperienceItemUseResult
                            {
                                Status = ExperienceItemUseStatus.Success,
                                AccountId = accountId,
                                ItemTemplateId = resolvedItemId,
                                ConsumedItem = consumedItem,
                                PreviousLevel = character.Level,
                                NewLevel = grant.NewLevel,
                                PreviousExp = character.Exp,
                                NewExp = grant.NewExp,
                                GrantedExp = usePlan.GrantedExp,
                                HonorExpGain = grant.HonorExpGain,
                                TotalHonorExp = grant.TotalHonorExp,
                                TotalGrowthCapsuleExp = totalGrowthCapsuleExp,
                                SyncedSkills = syncedSkills.Skills,
                                SkillPoints = SkillStateService.GetProtocolState(
                                    syncedSkills.Skills,
                                    syncedSkills.Points),
                            };

                            transaction.Commit();
                            inventory.ClearDirtyState();
                            sourceConsumed = false;

                            try
                            {
                                cooldownReservation?.Commit();
                            }
                            catch (Exception ex)
                            {
                                FileLogger.Log(
                                    $"[ExperienceItem] cooldown commit failed after database commit: item={resolvedItemId} cid={characterId} error={ex.Message}");
                            }

                            return result;
                        }
                    }
                }
            }
            catch (SqliteException ex)
            {
                if (sourceConsumed)
                    RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);

                FileLogger.Log(
                    $"[ExperienceItem] SQLite failure item={resolvedItemId} cid={characterId} slot={slotIndex}: {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode} {ex.Message}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "database transaction failed");
            }
            catch (Exception ex) when (sourceConsumed)
            {
                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                FileLogger.Log(
                    $"[ExperienceItem] inventory mutation rollback item={resolvedItemId} cid={characterId} slot={slotIndex}: {ex.Message}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "inventory transaction failed");
            }
            finally
            {
                cooldownReservation?.Dispose();
            }
        }

        private static InventoryMutationResult BuildConsumedMutation(
            InventoryListType listType,
            short slotIndex,
            ItemCore source,
            InventoryDeleteResult deleteResult)
        {
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = source != null ? source.ItemId : 0,
                RemainingStackCount = deleteResult != null ? deleteResult.RemainingCount : 0,
                InstanceValue = source != null && InventoryStackRuleService.IsStackable(source)
                    ? (deleteResult != null ? deleteResult.RemainingCount : 0)
                    : (source != null ? source.InstanceValue : 0),
                Durability = source != null ? source.Durability : (ushort)0,
                ExpireTime = source != null ? source.ExpireTime : 0,
                RequestedCount = 1,
                AppliedCount = (short)(deleteResult != null ? deleteResult.DeletedCount : 0),
            };
        }

        private static ExperienceItemUsePlan ValidateSkillPointBookUse(
            SkillPointBookDefinition definition,
            ItemCore source,
            CharacterProgressSnapshot character,
            uint nowUnixTime)
        {
            if (definition == null || !definition.IsSupported)
            {
                return new ExperienceItemUsePlan
                {
                    Status = ExperienceItemUseStatus.UnsupportedDefinition,
                    Detail = definition?.UnsupportedReason
                        ?? "skill-point book definition is unavailable",
                };
            }

            if ((source != null
                    && source.ExpireTime > 0
                    && (uint)source.ExpireTime <= nowUnixTime)
                || !definition.IsTemplateAvailableAt(nowUnixTime))
            {
                return new ExperienceItemUsePlan
                {
                    Status = ExperienceItemUseStatus.Expired,
                    Detail = "item has expired",
                };
            }

            if (definition.UsablePeriodDays > 0 && (source?.ExpireTime ?? 0) <= 0)
            {
                return new ExperienceItemUsePlan
                {
                    Status = ExperienceItemUseStatus.Expired,
                    Detail = "timed item has no instance expiration",
                };
            }

            if (character == null
                || (definition.MinimumLevel >= 0 && character.Level < definition.MinimumLevel)
                || (definition.MaximumLevel >= 0 && character.Level > definition.MaximumLevel))
            {
                return new ExperienceItemUsePlan
                {
                    Status = ExperienceItemUseStatus.LevelRestricted,
                    Detail = $"level={character?.Level ?? 0} allowed={definition.MinimumLevel}..{definition.MaximumLevel}",
                };
            }

            return new ExperienceItemUsePlan
            {
                Status = ExperienceItemUseStatus.Success,
            };
        }

        private static void RestoreConsumedSource(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            ItemCore sourceSnapshot)
        {
            if (inventory == null || sourceSnapshot == null)
                return;

            inventory.SetItem(listType, slotIndex, sourceSnapshot.Copy());
        }

        private static ExperienceItemUseResult Reject(
            ExperienceItemUseStatus status,
            int itemTemplateId,
            string detail)
            => new ExperienceItemUseResult
            {
                Status = status,
                ItemTemplateId = itemTemplateId,
                Detail = detail,
            };
    }
}
