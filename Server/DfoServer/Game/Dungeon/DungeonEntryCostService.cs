using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonEntryItemRequirement
    {
        internal DungeonEntryItemRequirement(
            int itemId,
            int count,
            bool consumeOnEntry)
        {
            ItemId = itemId;
            Count = count;
            ConsumeOnEntry = consumeOnEntry;
        }

        internal int ItemId { get; }
        internal int Count { get; }
        internal bool ConsumeOnEntry { get; }
    }

    internal sealed class DungeonEntryCostService
    {
        private readonly Func<InventoryLease, bool> _persistInventory;

        internal DungeonEntryCostService(
            Func<InventoryLease, bool> persistInventory = null)
        {
            _persistInventory = persistInventory
                ?? InventoryPersistenceService.SaveDirty;
        }

        internal static IReadOnlyList<DungeonEntryItemRequirement>
            ProjectPvfRequiredItems(
                IReadOnlyList<PvfLib.DungeonRequiredItem> source)
        {
            if (source == null)
                return null;
            if (source.Count == 0)
                return Array.Empty<DungeonEntryItemRequirement>();

            var result = new List<DungeonEntryItemRequirement>(source.Count);
            foreach (var item in source)
            {
                result.Add(item == null
                    ? default
                    : new DungeonEntryItemRequirement(
                        item.ItemId,
                        item.Count,
                        item.ConsumeOnEntry));
            }
            return result;
        }

        internal EntryCostResult TryConsumeRequiredItems(
            InventoryLease lease,
            IReadOnlyList<DungeonEntryItemRequirement> requiredItems)
        {
            var result = new EntryCostResult();
            if (lease?.Inventory == null)
                return result.Fail(
                    "inventory lease is missing",
                    EntryCostFailureKind.InvalidState);

            if (requiredItems == null)
                return result.Fail(
                    "required item definition is missing",
                    EntryCostFailureKind.Unavailable);

            if (requiredItems.Count == 0)
            {
                result.Success = true;
                return result;
            }

            if (!TryNormalizeRequiredItems(
                    requiredItems,
                    out var requiredCounts,
                    out var consumedCounts,
                    out var failureReason))
            {
                return result.Fail(
                    failureReason,
                    EntryCostFailureKind.Unavailable);
            }

            EntryItemSnapshot snapshot = null;
            lock (lease.SyncRoot)
            {
                try
                {
                    foreach (var requirement in requiredCounts)
                    {
                        var current = lease.Inventory.CountMainItem(
                            requirement.Key);
                        if (current < requirement.Value)
                        {
                            result.MissingItemId = requirement.Key;
                            result.RequiredCount = requirement.Value;
                            result.AvailableCount = current;
                            return result.Fail(
                                $"entry item missing item={requirement.Key} " +
                                $"need={requirement.Value} have={current}",
                                EntryCostFailureKind.MissingRequiredItem);
                        }
                    }

                    if (consumedCounts.Count == 0)
                    {
                        result.Success = true;
                        return result;
                    }

                    snapshot = EntryItemSnapshot.Capture(
                        lease.Inventory,
                        consumedCounts.Keys);
                    var requirements = consumedCounts
                        .OrderBy(pair => pair.Key)
                        .Select(pair => new InventoryMaterialRequirement(
                            pair.Key,
                            pair.Value))
                        .ToList();
                    var consumed = new List<InventoryMaterialConsumptionEntry>();
                    if (!InventoryMaterialConsumptionService.TryConsume(
                            lease.Inventory,
                            requirements,
                            consumed))
                    {
                        snapshot.Restore(lease.Inventory);
                        return result.Fail(
                            "entry item consumption failed",
                            EntryCostFailureKind.InvalidState);
                    }

                    if (!_persistInventory(lease))
                    {
                        snapshot.Restore(lease.Inventory);
                        return result.Fail(
                            "entry item persistence failed",
                            EntryCostFailureKind.InvalidState);
                    }

                    foreach (var entry in consumed)
                    {
                        result.ConsumedItems.Add(new ItemConsumeUpdate
                        {
                            ItemId = entry.ItemTemplateId,
                            Count = entry.Count,
                            SlotIndex = entry.SlotIndex,
                            RemainingCount = ResolveRemainingCount(
                                lease.Inventory,
                                entry.SlotIndex),
                        });
                    }

                    result.Success = true;
                    return result;
                }
                catch (Exception ex)
                {
                    snapshot?.Restore(lease.Inventory);
                    FileLogger.Log(
                        $"[DungeonEntryCost] TryConsumeRequiredItems ERROR: " +
                        ex.Message);
                    return result.Fail(
                        ex.Message,
                        EntryCostFailureKind.InvalidState);
                }
            }
        }

        internal EntryCostResult TryConsumePreferredAlternative(
            InventoryLease lease,
            IReadOnlyList<IReadOnlyList<DungeonEntryItemRequirement>>
                alternatives)
        {
            var result = new EntryCostResult();
            if (lease?.Inventory == null)
                return result.Fail(
                    "inventory lease is missing",
                    EntryCostFailureKind.InvalidState);
            if (alternatives == null || alternatives.Count == 0)
                return result.Fail(
                    "entry item alternatives are missing",
                    EntryCostFailureKind.Unavailable);

            lock (lease.SyncRoot)
            {
                for (var index = 0; index < alternatives.Count; index++)
                {
                    if (!TryNormalizeRequiredItems(
                            alternatives[index],
                            out _,
                            out _,
                            out var failureReason))
                    {
                        return result.Fail(
                            $"entry item alternative {index} is invalid: " +
                            failureReason,
                            EntryCostFailureKind.Unavailable);
                    }
                }

                for (var index = 0; index < alternatives.Count; index++)
                {
                    if (!HasRequiredItems(
                            lease.Inventory,
                            alternatives[index]))
                    {
                        continue;
                    }

                    result = TryConsumeRequiredItems(
                        lease,
                        alternatives[index]);
                    result.AlternativeIndex = index;
                    return result;
                }

                // Use the primary alternative to retain the canonical missing
                // item and quantity in the protocol rejection context.
                result = TryConsumeRequiredItems(lease, alternatives[0]);
                result.AlternativeIndex = -1;
                return result;
            }
        }

        internal EntryCostResult TryConsumeAbyssPartyTicket(
            InventoryLease lease,
            WorldMapArea area, int dungeonMinLevel)
        {
            var result = new EntryCostResult();
            if (lease?.Inventory == null || lease.CharacterId <= 0)
                return result.Fail(
                    "invalid character",
                    EntryCostFailureKind.InvalidState);

            if (area == null)
                return result.Fail(
                    "worldmap area missing",
                    EntryCostFailureKind.Unavailable);

            if (!area.HellDungeon)
                return result.Fail(
                    "area is not hell dungeon",
                    EntryCostFailureKind.Unavailable);

            if (!CheckHellQuestRequirement(lease.CharacterId, area, out var missingQuestId))
                return result.Fail(
                    $"hell quest not cleared quest={missingQuestId}",
                    EntryCostFailureKind.MissingPermission);

            try
            {
                foreach (var ticket in area.HellFreePassItems)
                {
                    if (ticket.ItemId <= 0 || ticket.Count <= 0)
                        continue;

                    if (lease.Inventory.CountMainItem(ticket.ItemId)
                        < ticket.Count)
                        continue;

                    result = TryConsumeRequiredItems(
                        lease,
                        new[]
                        {
                            new DungeonEntryItemRequirement(
                                ticket.ItemId,
                                ticket.Count,
                                consumeOnEntry: true),
                        });
                    if (result.Success)
                        result.IsFreePass = true;
                    return result;
                }

                var normalNeedCount = WorldMap.GetHellNormalTicketNeedCount(dungeonMinLevel);
                if (normalNeedCount <= 0)
                    return result.Fail(
                        $"dungeon min level too low minLevel={dungeonMinLevel}",
                        EntryCostFailureKind.Unavailable);

                var normalTicketItemIds = area.HellNormalTicketItemIds;
                if (normalTicketItemIds.Count == 0)
                    return result.Fail(
                        "normal ticket item missing",
                        EntryCostFailureKind.Unavailable);

                var selectedNormalTicketItemId = 0;
                foreach (var itemId in normalTicketItemIds)
                {
                    if (itemId > 0
                        && lease.Inventory.CountMainItem(itemId)
                            >= normalNeedCount)
                    {
                        selectedNormalTicketItemId = itemId;
                        break;
                    }
                }

                if (selectedNormalTicketItemId <= 0)
                {
                    var missingItemId = normalTicketItemIds
                        .FirstOrDefault(itemId => itemId > 0);
                    result.MissingItemId = missingItemId;
                    result.RequiredCount = normalNeedCount;
                    result.AvailableCount = missingItemId <= 0
                        ? 0
                        : lease.Inventory.CountMainItem(missingItemId);
                    return result.Fail(
                        $"ticket missing normalNeed={normalNeedCount}",
                        EntryCostFailureKind.MissingRequiredItem);
                }

                result = TryConsumeRequiredItems(
                    lease,
                    new[]
                    {
                        new DungeonEntryItemRequirement(
                            selectedNormalTicketItemId,
                            normalNeedCount,
                            consumeOnEntry: true),
                    });
                if (result.Success)
                    result.IsFreePass = false;
                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonEntryCost] TryConsumeAbyssPartyTicket ERROR: {ex.Message}");
                return result.Fail(
                    ex.Message,
                    EntryCostFailureKind.InvalidState);
            }
        }

        internal bool CheckHellQuestRequirement(
            int characterId,
            WorldMapArea area,
            out int missingQuestId)
        {
            var connStr = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath, ServerPaths.SchemaFilePath);
            var repository = new QuestRepository(connStr);
            return EvaluateHellQuestRequirement(
                area,
                questId => questId <= ushort.MaxValue
                    && repository.IsQuestCleared(characterId, (ushort)questId),
                out missingQuestId);
        }

        internal static bool EvaluateHellQuestRequirement(
            WorldMapArea area,
            Func<int, bool> isQuestCleared,
            out int missingQuestId)
        {
            missingQuestId = 0;
            if (area == null)
                return false;
            if (area.HellQuestIds.Count == 0)
                return true;
            if (isQuestCleared == null)
                throw new ArgumentNullException(nameof(isQuestCleared));

            foreach (var questId in area.HellQuestIds)
            {
                if (questId <= 0)
                    continue;

                if (!isQuestCleared(questId))
                {
                    missingQuestId = questId;
                    return false;
                }
            }

            return true;
        }

        private static bool TryNormalizeRequiredItems(
            IReadOnlyList<DungeonEntryItemRequirement> requiredItems,
            out Dictionary<int, int> requiredCounts,
            out Dictionary<int, int> consumedCounts,
            out string failureReason)
        {
            requiredCounts = new Dictionary<int, int>();
            consumedCounts = new Dictionary<int, int>();
            failureReason = string.Empty;
            if (requiredItems == null)
            {
                failureReason = "entry item requirement list is missing";
                return false;
            }
            foreach (var item in requiredItems)
            {
                if (item.ItemId <= 0 || item.Count <= 0)
                {
                    failureReason =
                        $"entry item definition is invalid item={item.ItemId} " +
                        $"count={item.Count}";
                    return false;
                }

                if (!TryAddCount(
                        requiredCounts,
                        item.ItemId,
                        item.Count))
                {
                    failureReason =
                        $"entry item count overflow item={item.ItemId}";
                    return false;
                }

                if (item.ConsumeOnEntry
                    && !TryAddCount(
                        consumedCounts,
                        item.ItemId,
                        item.Count))
                {
                    failureReason =
                        $"entry item consume count overflow item={item.ItemId}";
                    return false;
                }
            }

            return true;
        }

        private static bool HasRequiredItems(
            InventoryService inventory,
            IReadOnlyList<DungeonEntryItemRequirement> requiredItems)
        {
            if (inventory == null
                || !TryNormalizeRequiredItems(
                    requiredItems,
                    out var requiredCounts,
                    out _,
                    out _))
            {
                return false;
            }

            foreach (var requirement in requiredCounts)
            {
                if (inventory.CountMainItem(requirement.Key)
                    < requirement.Value)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryAddCount(
            IDictionary<int, int> counts,
            int itemId,
            int count)
        {
            var total = (long)(counts.TryGetValue(itemId, out var current)
                ? current
                : 0) + count;
            if (total > int.MaxValue)
                return false;
            counts[itemId] = (int)total;
            return true;
        }

        private static int ResolveRemainingCount(
            InventoryService inventory,
            short slotIndex)
        {
            if (inventory == null)
                return 0;
            if (InventoryService.IsVirtualMainSlot(slotIndex))
                return inventory.GetMainVirtualCount(slotIndex)?.Count ?? 0;
            var item = inventory.GetItem(InventoryListType.Main, slotIndex);
            if (item == null)
                return 0;
            return InventoryStackRuleService.IsStackable(item)
                ? Math.Max(0, item.Count)
                : 1;
        }

        private sealed class EntryItemSnapshot
        {
            private readonly Dictionary<short, ItemCore> _items =
                new Dictionary<short, ItemCore>();
            private readonly Dictionary<short, VirtualCountItem> _virtualItems =
                new Dictionary<short, VirtualCountItem>();

            internal static EntryItemSnapshot Capture(
                InventoryService inventory,
                IEnumerable<int> itemIds)
            {
                var snapshot = new EntryItemSnapshot();
                foreach (var itemId in itemIds.Distinct())
                {
                    if (InventoryService.TryResolveMainVirtualSlotByItemId(
                            itemId,
                            out var virtualSlot,
                            out _))
                    {
                        var virtualItem = inventory.GetMainVirtualCount(
                            virtualSlot);
                        if (virtualItem != null)
                            snapshot._virtualItems[virtualSlot] =
                                virtualItem.Copy();
                        continue;
                    }

                    foreach (var pair in inventory.GetItems(
                                 InventoryListType.Main))
                    {
                        if (pair.Value != null
                            && pair.Value.ItemId == itemId
                            && !snapshot._items.ContainsKey(pair.Key))
                        {
                            snapshot._items[pair.Key] = pair.Value.Copy();
                        }
                    }
                }

                return snapshot;
            }

            internal void Restore(InventoryService inventory)
            {
                if (inventory == null)
                    return;
                foreach (var pair in _items)
                {
                    inventory.SetItem(
                        InventoryListType.Main,
                        pair.Key,
                        pair.Value.Copy());
                }
                foreach (var pair in _virtualItems)
                {
                    inventory.SetMainVirtualCount(
                        pair.Key,
                        pair.Value.ItemId,
                        pair.Value.Count);
                }
            }
        }
    }

    internal sealed class EntryCostResult
    {
        public bool Success;
        public bool IsFreePass;
        public string FailReason;
        public EntryCostFailureKind FailureKind;
        public int MissingItemId;
        public int RequiredCount;
        public int AvailableCount;
        public int AlternativeIndex = -1;
        public List<ItemConsumeUpdate> ConsumedItems { get; } = new List<ItemConsumeUpdate>();

        internal EntryCostResult Fail(
            string reason,
            EntryCostFailureKind failureKind)
        {
            FailReason = reason;
            FailureKind = failureKind;
            return this;
        }
    }

    internal enum EntryCostFailureKind : byte
    {
        None = 0,
        InvalidState = 1,
        Unavailable = 2,
        MissingPermission = 3,
        MissingRequiredItem = 4,
    }

    internal sealed class ItemConsumeUpdate
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
        public short SlotIndex { get; set; }
        public int RemainingCount { get; set; }
    }
}
