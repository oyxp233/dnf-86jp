using DfoServer.Game.TitleBook;
using DfoServer.Game.Inventory;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class TitleBookUseItemAchievementSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== TITLEBOOK_USE_ITEM_ACHIEVEMENT selftest ===");
            var failures = 0;
            var provider = TitleBookStaticDataProvider.LoadDefault();

            var colorless = provider.GetUseItemQuests(3037)
                .SingleOrDefault(quest => quest.QuestId == 6532);
            Check(
                "PVF maps colorless cube consumption to quest 6532",
                colorless != null
                    && colorless.CheckCount == 10000
                    && colorless.RewardTitleItemId == 26648
                    && colorless.GetUseItemProgressPerItem(3037) == 1,
                ref failures);

            var mergedColorless = provider.BuildUseItemProgressDeltas(
                new[]
                {
                    new KeyValuePair<int, int>(3037, 2),
                    new KeyValuePair<int, int>(3037, 3),
                });
            Check(
                "same skill material is merged before achievement mutation",
                mergedColorless.TryGetValue(6532, out var colorlessDelta)
                    && colorlessDelta == 5,
                ref failures);

            var mergedAlternatives = provider.BuildUseItemProgressDeltas(
                new[]
                {
                    new KeyValuePair<int, int>(36, 2),
                    new KeyValuePair<int, int>(898, 3),
                });
            Check(
                "different accepted items for one PVF quest merge by quest id",
                mergedAlternatives.TryGetValue(6598, out var alternativeDelta)
                    && alternativeDelta == 5,
                ref failures);

            Check(
                "only skill-material operation types are eligible",
                !InventoryHandler.IsSkillMaterialDeleteOperation(0)
                    && !InventoryHandler.IsSkillMaterialDeleteOperation(1)
                    && InventoryHandler.IsSkillMaterialDeleteOperation(2)
                    && InventoryHandler.IsSkillMaterialDeleteOperation(ushort.MaxValue),
                ref failures);

            var sessionId = Guid.NewGuid();
            var inventory = new InventoryService(9906532, 1);
            InventoryContext.Register(sessionId, inventory);
            try
            {
                var mutation = new TitleBookMutationService("selftest");
                var initial = mutation.TriggerUseItemAchievements(
                    inventory.CharacterId,
                    3037,
                    9999).Single();
                Check(
                    "server-authoritative progress stores remaining count",
                    initial.Success
                        && initial.Remain1 == 1
                        && !initial.Completed,
                    ref failures);

                var completion = mutation.TriggerUseItemAchievements(
                    inventory.CharacterId,
                    3037,
                    1).Single();
                var awardedTitle = inventory.TitleBook.GetItem(1, 0);
                Check(
                    "zero transition awards Ultimate Colorless title once",
                    completion.Completed
                        && completion.Remain1 == 0
                        && completion.Category == 1
                        && completion.BookIndex == 0
                        && completion.TitleItemId == 26648
                        && awardedTitle?.ItemId == 26648,
                    ref failures);

                var repeated = mutation.TriggerUseItemAchievements(
                    inventory.CharacterId,
                    3037,
                    1).Single();
                Check(
                    "completed achievement is idempotent",
                    repeated.Success
                        && repeated.Remain1 == 0
                        && !repeated.Completed
                        && repeated.TitleItemId < 0,
                    ref failures);
            }
            finally
            {
                inventory.ClearDirtyState();
                InventoryContext.Unregister(sessionId, inventory.CharacterId);
            }

            Console.WriteLine($"=== result: {7 - failures} PASS, {failures} FAIL ===");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string label, bool success, ref int failures)
        {
            Console.WriteLine($"  [{(success ? "PASS" : "FAIL")}] {label}");
            if (!success)
                failures++;
        }
    }
}
