using System;
using DfoServer.Game.Inventory;

namespace DfoServer.SelfTests
{
    public static class PetSatietySelfTest
    {
        private const int AccountId = 163101;
        private const int CharacterId = 163101;
        private const int EquippedPetItemTemplateId = 100330649;
        private const int PetCreatureKey = 1;

        public static int Run()
        {
            Console.WriteLine("=== PET_SATIETY selftest ===");

            var failures = 0;
            var t0 = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

            // 非死亡检查只读不写，避免与随后的 ApplyDungeonElapsed 重复扣减同一区间。
            var inventory = CreateInventoryWithEquippedPet(40);
            var check = PetCreatureSatietyService.ApplyDungeonDeathIfExpired(
                inventory,
                t0,
                t0.AddSeconds(60));
            Check("non-death check reports computed satiety",
                check.CreatureKey == PetCreatureKey && check.Before == 40 && check.After == 39,
                ref failures);
            Check("non-death check does not persist satiety",
                inventory.CreatureDetails.GetDetail(PetCreatureKey)?.Stomach == 40,
                ref failures);

            var elapsed = PetCreatureSatietyService.ApplyDungeonElapsed(
                inventory,
                t0,
                t0.AddSeconds(60));
            Check("dungeon elapsed consumes each interval once",
                elapsed.Before == 40 && elapsed.After == 39 && elapsed.ConsumedSatiety == 1,
                ref failures);
            Check("dungeon elapsed persists single consumption",
                inventory.CreatureDetails.GetDetail(PetCreatureKey)?.Stomach == 39,
                ref failures);

            // 连续两次"检查+结算"模拟两分钟 tick，总消耗必须是 2 而不是 4。
            var t1 = t0.AddSeconds(60);
            PetCreatureSatietyService.ApplyDungeonDeathIfExpired(inventory, t1, t1.AddSeconds(60));
            PetCreatureSatietyService.ApplyDungeonElapsed(inventory, t1, t1.AddSeconds(60));
            Check("two ticks consume two satiety in total",
                inventory.CreatureDetails.GetDetail(PetCreatureKey)?.Stomach == 38,
                ref failures);

            // 到达死亡阈值仍然判死并写回 0。
            var dying = CreateInventoryWithEquippedPet(1);
            var death = PetCreatureSatietyService.ApplyDungeonDeathIfExpired(
                dying,
                t0,
                t0.AddSeconds(120));
            Check("expired creature dies",
                death.After == 0 && death.Changed,
                ref failures);
            Check("expired creature satiety persisted as zero",
                dying.CreatureDetails.GetDetail(PetCreatureKey)?.Stomach == 0,
                ref failures);

            // 存活下限：未死亡时可见饱食度保持 1，不会提前写成 0。
            var low = CreateInventoryWithEquippedPet(2);
            var lowUpdate = PetCreatureSatietyService.ApplyDungeonElapsed(
                low,
                t0,
                t0.AddSeconds(90));
            Check("alive creature keeps visible minimum of 1",
                lowUpdate.After == 1
                    && low.CreatureDetails.GetDetail(PetCreatureKey)?.Stomach == 1,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static InventoryService CreateInventoryWithEquippedPet(int satiety)
        {
            var inventory = new InventoryService(CharacterId, AccountId);
            var equippedPet = ItemCore.Create(ItemCore.KindCreature, EquippedPetItemTemplateId);
            equippedPet.Value = PetCreatureKey;
            inventory.SetItem(InventoryListType.Equipment, PetInventoryLayout.CreatureEquipSlot, equippedPet);
            inventory.CreatureDetails.Put(new CreatureDetail
            {
                Uid = PetCreatureKey,
                Field04 = 0,
                ModeFlag = 0,
                ProgressValue32 = 0,
                FieldAfterValue32 = 1,
            });
            inventory.CreatureDetails.GetDetail(PetCreatureKey).Stomach =
                (byte)Math.Max(0, Math.Min(100, satiety));
            return inventory;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
    }
}
