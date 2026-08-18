using DfoServer.Game.Inventory;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class CompoundRecipeUpgradeSelfTest
    {
        private const short SourceSlot = 11;
        private const short OutputSlot = 24;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== COMPOUND_RECIPE_UPGRADE selftest ===");
            TestApplyUpgradeAttributes();
            TestGuardAndEndToEnd();
            Console.WriteLine(_failures == 0
                ? "CompoundRecipeUpgradeSelfTest OK"
                : $"CompoundRecipeUpgradeSelfTest FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestApplyUpgradeAttributes()
        {
            var source = new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = 101010653,
            };
            source.Upgrade = 12;
            source.ReSealCount = 5;
            source.AmplifyType = 2;
            source.AmplifyValue = 0x1234;
            source.GenuineUpgrade = 7;
            source.EnchantCardId = 0x01020304;
            source.EnchantUpgradeCount = 3;

            var target = new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = 101010900,
            };
            target.Upgrade = 0;
            target.ReSealCount = 1;

            InventoryCompoundItemRecipeService.ApplyUpgradeAttributes(target, source);

            Check(target.Upgrade == 12, "Upgrade copied (+12)");
            Check(target.AmplifyType == 2, "AmplifyType copied (增幅属性)");
            Check(target.AmplifyValue == 0x1234, "AmplifyValue copied (增幅数值)");
            Check(target.GenuineUpgrade == 7, "GenuineUpgrade copied (锻造)");
            Check(target.EnchantCardId == 0x01020304, "EnchantCardId copied (附魔卡片)");
            Check(target.EnchantUpgradeCount == 3, "EnchantUpgradeCount copied (附魔升级次数)");
            Check(target.ReSealCount == 1, "target ReSealCount preserved (不覆盖高位)");
            Check(source.ReSealCount == 5, "source ReSealCount untouched");
        }

        private static void TestGuardAndEndToEnd()
        {
            int sourceItemId;
            int outputItemId;
            try
            {
                if (!TryPickTwoEquipmentIds(out sourceItemId, out outputItemId))
                {
                    Console.WriteLine("  [SKIP] real PVF equipment ids unavailable (Script.pvf missing)");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [SKIP] PVF equipment enumeration failed: {ex.Message}");
                return;
            }

            TestGuardRejectsMultipleEquipmentMaterials(sourceItemId, outputItemId);
            TestGuardRejectsMultipleEquipmentOutputs(sourceItemId, outputItemId);
            TestGuardRejectsNonEquipmentOutput(sourceItemId);
            TestEndToEndPreservesAttributes(sourceItemId, outputItemId);
        }

        private static void TestGuardRejectsMultipleEquipmentMaterials(int sourceItemId, int outputItemId)
        {
            var inventory = CreateInventoryWithWeapon(sourceItemId);
            var materials = new[]
            {
                new CompoundItemRecipeEntry(sourceItemId, 1),
                new CompoundItemRecipeEntry(outputItemId, 1),
            };
            var outputs = new[] { new CompoundItemRecipeEntry(outputItemId, 1) };

            InventoryCompoundItemRecipeService.TryResolveUpgradeAttributeSource(
                inventory, materials, outputs, out var attributeSource);

            Check(attributeSource == null, "guard rejects two equipment materials");
        }

        private static void TestGuardRejectsMultipleEquipmentOutputs(int sourceItemId, int outputItemId)
        {
            var inventory = CreateInventoryWithWeapon(sourceItemId);
            var materials = new[] { new CompoundItemRecipeEntry(sourceItemId, 1) };
            var outputs = new[]
            {
                new CompoundItemRecipeEntry(sourceItemId, 1),
                new CompoundItemRecipeEntry(outputItemId, 1),
            };

            InventoryCompoundItemRecipeService.TryResolveUpgradeAttributeSource(
                inventory, materials, outputs, out var attributeSource);

            Check(attributeSource == null, "guard rejects two equipment outputs");
        }

        private static void TestGuardRejectsNonEquipmentOutput(int sourceItemId)
        {
            var inventory = CreateInventoryWithWeapon(sourceItemId);
            var materials = new[] { new CompoundItemRecipeEntry(sourceItemId, 1) };
            var outputs = new[] { new CompoundItemRecipeEntry(3326, 10) };

            InventoryCompoundItemRecipeService.TryResolveUpgradeAttributeSource(
                inventory, materials, outputs, out var attributeSource);

            Check(attributeSource == null, "guard rejects non-equipment output (珠子合成不转移)");
        }

        private static void TestEndToEndPreservesAttributes(int sourceItemId, int outputItemId)
        {
            var inventory = new InventoryService(950001, 950002);
            var weapon = CreateUpgradedWeapon(sourceItemId);
            inventory.SetItem(InventoryListType.Main, SourceSlot, weapon);

            var expectedUpgrade = weapon.Upgrade;
            var expectedAmplifyType = weapon.AmplifyType;
            var expectedAmplifyValue = weapon.AmplifyValue;
            var expectedGenuineUpgrade = weapon.GenuineUpgrade;
            var expectedEnchantCardId = weapon.EnchantCardId;
            var expectedEnchantUpgradeCount = weapon.EnchantUpgradeCount;

            var materials = new[] { new CompoundItemRecipeEntry(sourceItemId, 1) };
            var outputs = new[] { new CompoundItemRecipeEntry(outputItemId, 1) };

            InventoryCompoundItemRecipeService.TryResolveUpgradeAttributeSource(
                inventory, materials, outputs, out var attributeSource);
            Check(attributeSource != null, "1:1 equipment upgrade resolves attribute source");

            var rewardRequests = InventoryCompoundItemRecipeService.BuildRewardRequests(outputs, attributeSource);
            Check(rewardRequests.Count == 1 && rewardRequests[0].UseExistingCore,
                "equipment output uses Existing core (attribute transfer)");
            Check(rewardRequests[0].Core != null
                && rewardRequests[0].Core.Upgrade == expectedUpgrade
                && rewardRequests[0].Core.AmplifyType == expectedAmplifyType
                && rewardRequests[0].Core.GenuineUpgrade == expectedGenuineUpgrade
                && rewardRequests[0].Core.EnchantCardId == expectedEnchantCardId,
                "request core carries source attributes before grant");

            inventory.RemoveItem(InventoryListType.Main, SourceSlot);

            if (!InventoryRewardGrantService.TryPlanBatch(inventory, rewardRequests, out var plan)
                || plan == null || !plan.Success)
            {
                Check(false, "TryPlanBatch succeeded for transferred output");
                return;
            }

            var plannedCore = plan.Entries.Count > 0 ? plan.Entries[0].Core : null;
            Check(plannedCore != null && plannedCore.Upgrade == expectedUpgrade,
                $"plan entry core carries attributes (Upgrade={plannedCore?.Upgrade ?? -1})");

            if (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, plan, out var grantBatch)
                || grantBatch == null || !grantBatch.Success)
            {
                Check(false, "TryApplyPreparedBatch succeeded for transferred output");
                return;
            }

            var granted = inventory.GetItems(InventoryListType.Main)
                .Select(p => p.Value)
                .FirstOrDefault(c => c != null && c.ItemId == outputItemId);
            Check(granted != null, "granted output weapon present in inventory");
            if (granted == null)
                return;

            Check(granted.Upgrade == expectedUpgrade, $"granted Upgrade == {expectedUpgrade} (强化/增幅等级)");
            Check(granted.AmplifyType == expectedAmplifyType, "granted AmplifyType preserved (增幅属性)");
            Check(granted.AmplifyValue == expectedAmplifyValue, "granted AmplifyValue preserved (增幅数值)");
            Check(granted.GenuineUpgrade == expectedGenuineUpgrade, "granted GenuineUpgrade preserved (锻造)");
            Check(granted.EnchantCardId == expectedEnchantCardId, "granted EnchantCardId preserved (附魔卡片)");
            Check(granted.EnchantUpgradeCount == expectedEnchantUpgradeCount, "granted EnchantUpgradeCount preserved (附魔升级)");
        }

        private static InventoryService CreateInventoryWithWeapon(int sourceItemId)
        {
            var inventory = new InventoryService(950001, 950002);
            inventory.SetItem(InventoryListType.Main, SourceSlot, CreateUpgradedWeapon(sourceItemId));
            return inventory;
        }

        private static ItemCore CreateUpgradedWeapon(int itemId)
        {
            var weapon = new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = itemId,
                Uid = 1,
                Durability = 40,
            };
            weapon.Upgrade = 12;
            weapon.AmplifyType = 2;
            weapon.AmplifyValue = 0x1234;
            weapon.GenuineUpgrade = 7;
            weapon.EnchantCardId = 0x01020304;
            weapon.EnchantUpgradeCount = 3;
            return weapon;
        }

        private static bool TryPickTwoEquipmentIds(out int first, out int second)
        {
            first = 0;
            second = 0;
            var entries = ItemMetadataResolver.EquipmentList.Value.Entries;
            if (entries == null || entries.Count == 0)
                return false;

            var picked = 0;
            foreach (var entry in entries)
            {
                if (!ItemMetadataResolver.TryResolveItemKind(entry.Id, out var kind)
                    || kind != ItemCore.KindEquipment)
                    continue;
                if (picked == 0)
                    first = entry.Id;
                else if (entry.Id != first)
                {
                    second = entry.Id;
                    return true;
                }
                picked++;
            }
            return false;
        }

        private static void Check(bool condition, string label)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {label}");
            if (!condition) _failures++;
        }
    }
}
