using DfoServer.Game.Dungeon;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class DungeonEntryCostSelfTest
    {
        private const int LoveMagathaDungeonId = 3541;
        private const int LoveMagathaInvitationId = 10002609;
        private const int TauKingdomDungeonId = 3700;
        private const int TauKingdomPassId = 690001556;
        private const int SouthernValleyDungeonId = 100;
        private const int MysteriousInvitationId = 2680738;
        private const int DeathTowerDungeonId = 11000;
        private const int TowerOfIllusionDungeonId = 11001;
        private const int DeathInvitationId = 4183;
        private const int SecretDeathInvitationId = 2683076;

        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_ENTRY_COST selftest ===");
            var failures = 0;

            CheckPvfRequiredItem(
                LoveMagathaDungeonId,
                LoveMagathaInvitationId,
                3,
                ref failures);
            CheckPvfRequiredItem(
                TauKingdomDungeonId,
                TauKingdomPassId,
                1,
                ref failures);
            CheckPvfRequiredItem(
                SouthernValleyDungeonId,
                MysteriousInvitationId,
                1,
                ref failures);
            CheckDeathTowerEntryItems(
                DeathTowerDungeonId,
                ref failures);
            CheckDeathTowerEntryItems(
                TowerOfIllusionDungeonId,
                ref failures);

            var loveItems = DungeonEntryCostService.ProjectPvfRequiredItems(
                GameWorld.Dungeon.GetDungeonFile(LoveMagathaDungeonId)
                    .RequiredItems);
            var persistCalls = 0;
            var service = new DungeonEntryCostService(_ =>
            {
                persistCalls++;
                return true;
            });

            var missingLease = CreateLease(characterId: 991401);
            var missing = service.TryConsumeRequiredItems(
                missingLease,
                loveItems);
            Check("missing PVF entry item rejects before persistence",
                !missing.Success
                && missing.FailureKind
                    == EntryCostFailureKind.MissingRequiredItem
                && missingLease.Inventory.CountMainItem(
                    LoveMagathaInvitationId) == 0
                && persistCalls == 0,
                ref failures);

            var splitLease = CreateLease(characterId: 991402);
            AddStack(
                splitLease.Inventory,
                slotIndex: 10,
                LoveMagathaInvitationId,
                count: 2);
            AddStack(
                splitLease.Inventory,
                slotIndex: 11,
                LoveMagathaInvitationId,
                count: 2);
            var consumed = service.TryConsumeRequiredItems(
                splitLease,
                loveItems);
            Check("PVF entry item consumes atomically across stacks",
                consumed.Success
                && consumed.ConsumedItems.Sum(item => item.Count) == 3
                && consumed.ConsumedItems.Count == 2
                && consumed.ConsumedItems.All(
                    item => item.ItemId == LoveMagathaInvitationId)
                && splitLease.Inventory.CountMainItem(
                    LoveMagathaInvitationId) == 1
                && splitLease.Inventory.GetItem(
                    InventoryListType.Main,
                    10) == null
                && splitLease.Inventory.GetItem(
                    InventoryListType.Main,
                    11)?.Count == 1
                && persistCalls == 1,
                ref failures);

            var retainedLease = CreateLease(characterId: 991403);
            AddStack(
                retainedLease.Inventory,
                slotIndex: 12,
                TauKingdomPassId,
                count: 1);
            var retained = service.TryConsumeRequiredItems(
                retainedLease,
                new[]
                {
                    new DungeonEntryItemRequirement(
                        TauKingdomPassId,
                        1,
                        consumeOnEntry: false),
                });
            Check("non-consuming PVF requirement validates without mutation",
                retained.Success
                && retained.ConsumedItems.Count == 0
                && retainedLease.Inventory.CountMainItem(
                    TauKingdomPassId) == 1
                && persistCalls == 1,
                ref failures);

            var rollbackLease = CreateLease(characterId: 991404);
            AddStack(
                rollbackLease.Inventory,
                slotIndex: 13,
                MysteriousInvitationId,
                count: 2);
            var rollbackService = new DungeonEntryCostService(_ => false);
            var rolledBack = rollbackService.TryConsumeRequiredItems(
                rollbackLease,
                new[]
                {
                    new DungeonEntryItemRequirement(
                        MysteriousInvitationId,
                        1,
                        consumeOnEntry: true),
                });
            Check("persistence failure restores every consumed entry item",
                !rolledBack.Success
                && rolledBack.FailureKind
                    == EntryCostFailureKind.InvalidState
                && rolledBack.ConsumedItems.Count == 0
                && rollbackLease.Inventory.CountMainItem(
                    MysteriousInvitationId) == 2
                && rollbackLease.Inventory.GetItem(
                    InventoryListType.Main,
                    13)?.Count == 2,
                ref failures);

            var towerAlternatives = CreateDeathTowerAlternatives();
            var towerService = new DungeonEntryCostService(_ => true);
            var primaryLease = CreateLease(characterId: 991405);
            AddStack(
                primaryLease.Inventory,
                slotIndex: 14,
                DeathInvitationId,
                count: 1);
            AddStack(
                primaryLease.Inventory,
                slotIndex: 15,
                SecretDeathInvitationId,
                count: 1);
            var primary = towerService.TryConsumePreferredAlternative(
                primaryLease,
                towerAlternatives);
            Check("death tower consumes the normal invitation first",
                primary.Success
                && primary.AlternativeIndex == 0
                && primaryLease.Inventory.CountMainItem(
                    DeathInvitationId) == 0
                && primaryLease.Inventory.CountMainItem(
                    SecretDeathInvitationId) == 1,
                ref failures);

            var fallbackLease = CreateLease(characterId: 991406);
            AddStack(
                fallbackLease.Inventory,
                slotIndex: 16,
                SecretDeathInvitationId,
                count: 1);
            var fallback = towerService.TryConsumePreferredAlternative(
                fallbackLease,
                towerAlternatives);
            Check("death tower falls back to the secret invitation",
                fallback.Success
                && fallback.AlternativeIndex == 1
                && fallbackLease.Inventory.CountMainItem(
                    SecretDeathInvitationId) == 0,
                ref failures);

            var noTowerTicketLease = CreateLease(characterId: 991407);
            var noTowerTicket = towerService.TryConsumePreferredAlternative(
                noTowerTicketLease,
                towerAlternatives);
            Check("death tower rejects when neither invitation exists",
                !noTowerTicket.Success
                && noTowerTicket.FailureKind
                    == EntryCostFailureKind.MissingRequiredItem
                && noTowerTicket.MissingItemId == DeathInvitationId
                && noTowerTicket.RequiredCount == 1
                && noTowerTicket.AlternativeIndex == -1,
                ref failures);

            var combinedPersistCalls = 0;
            var combinedService = new DungeonEntryCostService(_ =>
            {
                combinedPersistCalls++;
                return true;
            });
            var combinedPlan = CreateCombinedEntryPlan();
            var combinedLease = CreateLease(characterId: 991408);
            AddStack(
                combinedLease.Inventory,
                slotIndex: 17,
                LoveMagathaInvitationId,
                count: 3);
            AddStack(
                combinedLease.Inventory,
                slotIndex: 18,
                TauKingdomPassId,
                count: 1);
            AddStack(
                combinedLease.Inventory,
                slotIndex: 19,
                DeathInvitationId,
                count: 24);
            combinedLease.Inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                count: 200000);
            var combinedValidation = combinedService.TryValidatePlan(
                combinedLease,
                combinedPlan);
            Check("combined admission validates without side effects",
                combinedValidation.Success
                && combinedValidation.IsFreePass
                && combinedPersistCalls == 0
                && combinedLease.Inventory.CountMainItem(
                    LoveMagathaInvitationId) == 3
                && combinedLease.Inventory.CountMainItem(
                    TauKingdomPassId) == 1
                && combinedLease.Inventory.CountMainItem(0) == 200000,
                ref failures);

            var combinedCommit = combinedService.TryCommitPlan(
                combinedLease,
                combinedPlan);
            Check("combined admission persists items and gold once",
                combinedCommit.Success
                && combinedCommit.IsFreePass
                && combinedCommit.GoldCost == 190000
                && combinedCommit.GoldBefore == 200000
                && combinedCommit.GoldAfter == 10000
                && combinedCommit.ConsumedItems.Sum(item => item.Count) == 4
                && combinedPersistCalls == 1
                && combinedLease.Inventory.CountMainItem(
                    LoveMagathaInvitationId) == 0
                && combinedLease.Inventory.CountMainItem(
                    TauKingdomPassId) == 0
                && combinedLease.Inventory.CountMainItem(
                    DeathInvitationId) == 24,
                ref failures);

            var combinedMissingLease = CreateLease(characterId: 991409);
            AddStack(
                combinedMissingLease.Inventory,
                slotIndex: 20,
                LoveMagathaInvitationId,
                count: 3);
            combinedMissingLease.Inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                count: 200000);
            var combinedMissing = combinedService.TryValidatePlan(
                combinedMissingLease,
                combinedPlan);
            Check("combined admission reports canonical normal ticket and consumes nothing",
                !combinedMissing.Success
                && combinedMissing.FailureKind
                    == EntryCostFailureKind.MissingRequiredItem
                && combinedMissing.MissingItemId == DeathInvitationId
                && combinedMissing.RequiredCount == 24
                && combinedMissingLease.Inventory.CountMainItem(
                    LoveMagathaInvitationId) == 3
                && combinedMissingLease.Inventory.CountMainItem(0) == 200000
                && combinedPersistCalls == 1,
                ref failures);

            var combinedRollbackLease = CreateLease(characterId: 991410);
            AddStack(
                combinedRollbackLease.Inventory,
                slotIndex: 21,
                LoveMagathaInvitationId,
                count: 3);
            AddStack(
                combinedRollbackLease.Inventory,
                slotIndex: 22,
                TauKingdomPassId,
                count: 1);
            combinedRollbackLease.Inventory.SetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                count: 200000);
            var combinedRollbackService =
                new DungeonEntryCostService(_ => false);
            var combinedRollback = combinedRollbackService.TryCommitPlan(
                combinedRollbackLease,
                combinedPlan);
            Check("combined admission rolls back every cost when persistence fails",
                !combinedRollback.Success
                && combinedRollback.FailureKind
                    == EntryCostFailureKind.InvalidState
                && combinedRollbackLease.Inventory.CountMainItem(
                    LoveMagathaInvitationId) == 3
                && combinedRollbackLease.Inventory.CountMainItem(
                    TauKingdomPassId) == 1
                && combinedRollbackLease.Inventory.CountMainItem(0) == 200000,
                ref failures);

            var applicationPersistCalls = 0;
            var application =
                new DungeonEntryAdmissionApplicationService(
                    new DungeonEntryCostService(_ =>
                    {
                        applicationPersistCalls++;
                        return true;
                    }));
            var applicationMissingLease = CreateLease(
                characterId: 991411);
            var applicationRun = new DungeonRun(
                LoveMagathaDungeonId,
                difficulty: 0);
            var applicationMissing = application.TryPrepareRun(
                applicationMissingLease,
                applicationRun,
                tournamentDefinition: null,
                manualHellParty: false,
                requestedHellDifficulty: 0,
                maze: null,
                mazeIndex: 0,
                gorgeousChallengeEnabled: false,
                out _,
                out var applicationMissingValidation);
            Check("application boundary resolves PVF entry requirements without mutation",
                !applicationMissing
                && applicationMissingValidation.FailureKind
                    == EntryCostFailureKind.MissingRequiredItem
                && applicationMissingValidation.MissingItemId
                    == LoveMagathaInvitationId
                && applicationPersistCalls == 0,
                ref failures);

            var applicationLease = CreateLease(characterId: 991412);
            AddStack(
                applicationLease.Inventory,
                slotIndex: 23,
                LoveMagathaInvitationId,
                count: 3);
            var applicationPrepared = application.TryPrepareRun(
                applicationLease,
                applicationRun,
                tournamentDefinition: null,
                manualHellParty: false,
                requestedHellDifficulty: 0,
                maze: null,
                mazeIndex: 0,
                gorgeousChallengeEnabled: false,
                out var applicationPreparation,
                out var applicationValidation);
            var applicationCommit = application.TryCommit(
                applicationLease,
                applicationPreparation);
            Check("application boundary commits PVF entry requirement once",
                applicationPrepared
                && applicationValidation.Success
                && applicationCommit.Success
                && applicationPersistCalls == 1
                && applicationLease.Inventory.CountMainItem(
                    LoveMagathaInvitationId) == 0,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckPvfRequiredItem(
            int dungeonId,
            int itemId,
            int count,
            ref int failures)
        {
            var dungeon = GameWorld.Dungeon.GetDungeonFile(dungeonId);
            var item = dungeon?.RequiredItems?.SingleOrDefault();
            Check($"dungeon {dungeonId} keeps its PVF required item",
                item != null
                && item.ItemId == itemId
                && item.Count == count
                && item.ConsumeOnEntry,
                ref failures);
        }

        private static void CheckDeathTowerEntryItems(
            int dungeonId,
            ref int failures)
        {
            var config = DeathTowerData.GetConfig(dungeonId);
            if (config == null)
            {
                Check(
                    $"death tower {dungeonId} keeps ordered PVF invitations",
                    false,
                    ref failures);
                return;
            }
            var required = config.RequiredEntryItems.SingleOrDefault();
            var added = config.AddedRequiredEntryItems.SingleOrDefault();
            Check($"death tower {dungeonId} keeps ordered PVF invitations",
                required.ItemId == DeathInvitationId
                && required.Count == 1
                && required.ConsumeOnEntry
                && added.ItemId == SecretDeathInvitationId
                && added.Count == 1
                && added.ConsumeOnEntry,
                ref failures);
        }

        private static IReadOnlyList<
            IReadOnlyList<DungeonEntryItemRequirement>>
            CreateDeathTowerAlternatives()
        {
            return new IReadOnlyList<DungeonEntryItemRequirement>[]
            {
                new[]
                {
                    new DungeonEntryItemRequirement(
                        DeathInvitationId,
                        1,
                        consumeOnEntry: true),
                },
                new[]
                {
                    new DungeonEntryItemRequirement(
                        SecretDeathInvitationId,
                        1,
                        consumeOnEntry: true),
                },
            };
        }

        private static DungeonEntryCostPlan CreateCombinedEntryPlan()
        {
            var plan = new DungeonEntryCostPlan("selftest-combined");
            plan.AddRequiredItems(new[]
            {
                new DungeonEntryItemRequirement(
                    LoveMagathaInvitationId,
                    3,
                    consumeOnEntry: true),
            });
            plan.AddAlternativeGroup(
                new[]
                {
                    new DungeonEntryCostAlternative(
                        new[]
                        {
                            new DungeonEntryItemRequirement(
                                TauKingdomPassId,
                                1,
                                consumeOnEntry: true),
                        },
                        isFreePass: true),
                    new DungeonEntryCostAlternative(
                        new[]
                        {
                            new DungeonEntryItemRequirement(
                                DeathInvitationId,
                                24,
                                consumeOnEntry: true),
                        }),
                },
                missingAlternativeIndex: 1);
            if (!plan.TryAddGoldCost(190000))
                throw new InvalidOperationException("failed to add gold cost");
            return plan;
        }

        private static InventoryLease CreateLease(int characterId)
        {
            return new InventoryLease(
                Guid.NewGuid(),
                characterId,
                new InventoryService(characterId, characterId),
                version: 1);
        }

        private static void AddStack(
            InventoryService inventory,
            short slotIndex,
            int itemId,
            int count)
        {
            if (!inventory.SetItem(
                    InventoryListType.Main,
                    slotIndex,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindConsumable,
                        ItemId = itemId,
                        Count = count,
                    }))
            {
                throw new InvalidOperationException(
                    $"failed to seed item={itemId} slot={slotIndex}");
            }
        }

        private static void Check(
            string name,
            bool ok,
            ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
