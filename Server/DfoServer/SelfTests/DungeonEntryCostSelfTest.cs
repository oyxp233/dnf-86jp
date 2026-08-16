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
