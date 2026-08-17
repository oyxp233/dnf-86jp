using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using System;

namespace DfoServer.SelfTests
{
    public static class HellPartySelectionEligibilitySelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== HELL_PARTY_SELECTION_ELIGIBILITY selftest ===");
            var failures = 0;

            Check("Gent east dungeon gate resolves world-map area 12",
                Town.TryGetDungeonGateWorldMapAreaId(6, 3, out var areaId)
                    && areaId == 12,
                ref failures);

            var area = WorldMap.GetAreaById(areaId);
            Check("resolved world-map area is a hell-party area",
                area != null && area.HellDungeon,
                ref failures);
            Check("resolved area keeps its PVF hell quest requirement",
                area != null
                    && area.HellQuestIds.Count > 0
                    && area.HellQuestIds.Contains(2613),
                ref failures);

            Check("uncleared PVF hell quest blocks selection",
                area != null
                    && !DungeonEntryCostService.EvaluateHellQuestRequirement(
                        area,
                        _ => false,
                        out var missingQuestId)
                    && missingQuestId == area.HellQuestIds[0],
                ref failures);
            Check("all cleared PVF hell quests unlock selection",
                area != null
                    && DungeonEntryCostService.EvaluateHellQuestRequirement(
                        area,
                        _ => true,
                        out var clearedMissingQuestId)
                    && clearedMissingQuestId == 0,
                ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
