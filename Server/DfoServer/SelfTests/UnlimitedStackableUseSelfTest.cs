using System;
using DfoServer.Game.Inventory;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static class UnlimitedStackableUseSelfTest
    {
        private const int AccountId = 163201;
        private const int CharacterId = 163201;
        private const int UnlimitedOwlItemId = 51; // 无限猫头鹰: [stackable type] `[unlimited waste]`
        private const int PetFoodItemId = 24; // 宠物饲料: [feed]，普通消耗品对照组
        private const short MainSlot = 70;

        public static int Run()
        {
            Console.WriteLine("=== UNLIMITED_STACKABLE_USE selftest ===");

            var failures = 0;
            Check("unlimited owl is detected as unlimited-use stackable",
                InventoryHandler.IsUnlimitedUseStackable(UnlimitedOwlItemId),
                ref failures);
            Check("ordinary consumable is not detected as unlimited-use",
                !InventoryHandler.IsUnlimitedUseStackable(PetFoodItemId),
                ref failures);
            Check("invalid item id is not detected as unlimited-use",
                !InventoryHandler.IsUnlimitedUseStackable(0),
                ref failures);

            var inventory = new InventoryService(CharacterId, AccountId);
            var owl = ItemCore.Create(ItemCore.KindConsumable, UnlimitedOwlItemId);
            owl.Count = 1;
            inventory.SetItem(InventoryListType.Main, MainSlot, owl);

            Check("unlimited owl passes use validation",
                InventoryDeleteService.CanUseStackableForClient(
                    inventory,
                    InventoryListType.Main,
                    MainSlot,
                    UnlimitedOwlItemId,
                    out var resolvedId) && resolvedId == UnlimitedOwlItemId,
                ref failures);

            // 记录通用消耗路径的行为：若未经 unlimited 拦截，数量为 1 的道具会被直接删光。
            // 该断言用于固定"拦截必须发生在通用消耗之前"这一前提。
            Check("generic use path would delete single-count owl without interception",
                InventoryDeleteService.TryUseStackableForClient(
                    inventory,
                    InventoryListType.Main,
                    MainSlot,
                    UnlimitedOwlItemId,
                    out _)
                    && inventory.GetItem(InventoryListType.Main, MainSlot) == null,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
    }
}
