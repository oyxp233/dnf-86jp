using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal sealed class DailyChallengeRewardDefinition
    {
        internal int GroupIndex { get; set; }
        internal int RequiredCompletionCount { get; set; }
        internal int ItemId { get; set; }
        internal int ItemCount { get; set; }
    }

    internal sealed class DailyChallengeGenerationPlan
    {
        internal List<DailyChallengeGenerationGroup> Groups { get; } =
            new List<DailyChallengeGenerationGroup>();

        internal DailyChallengeSpecialDefinition SpecialChallenge { get; set; }
    }

    internal sealed class DailyChallengeSpecialDefinition
    {
        internal int ChallengeType { get; set; }
        internal int MinimumLevel { get; set; }
        internal int MaximumLevel { get; set; }
        internal int TargetValue { get; set; }
    }

    internal sealed class DailyChallengeGenerationGroup
    {
        internal int GroupIndex { get; set; }
        internal int GroupId { get; set; }
        internal List<DailyChallengeGenerationEntry> Entries { get; } =
            new List<DailyChallengeGenerationEntry>();
    }

    internal sealed class DailyChallengeGenerationEntry
    {
        internal int EntryIndex { get; set; }
        internal int QuestId { get; set; }
        internal uint TargetValue { get; set; }
    }

    internal static class DailyChallengeData
    {
        private const string ConfigPath = "etc/dailychallengetable.etc";

        private static readonly Lazy<DailyChallengeCatalog> Catalog =
            new Lazy<DailyChallengeCatalog>(LoadCatalog);

        internal static bool IsConfiguredQuest(int questId) =>
            Catalog.Value.QuestIds.Contains(questId);

        internal static DailyChallengeGenerationPlan BuildGenerationPlan(
            int characterId,
            int characterLevel,
            int dayId)
        {
            var plan = new DailyChallengeGenerationPlan();
            plan.SpecialChallenge = ResolveSpecialChallenge(characterLevel);
            foreach (var group in Catalog.Value.Groups)
            {
                if (characterLevel < group.MinimumLevel
                    || characterLevel > group.MaximumLevel)
                {
                    continue;
                }

                var activeSlotCount = group.ResolveActiveSlotCount(characterLevel);
                if (activeSlotCount <= 0)
                    activeSlotCount = group.Slots.Count;
                activeSlotCount = Math.Min(activeSlotCount, group.Slots.Count);
                if (activeSlotCount <= 0)
                    continue;

                var generatedGroup = new DailyChallengeGenerationGroup
                {
                    GroupIndex = group.GroupIndex,
                    // The 86JP wire/database layout uses the zero-based PVF group
                    // order for both fields. This also matches the historical seed.
                    GroupId = group.GroupIndex,
                };
                for (var entryIndex = 0; entryIndex < activeSlotCount; entryIndex++)
                {
                    var slot = group.Slots[entryIndex];
                    var candidates = new List<int>();
                    foreach (var questId in slot.QuestIds)
                    {
                        if (QuestData.IsDailyChallengeQuest(questId))
                            candidates.Add(questId);
                    }

                    if (candidates.Count == 0)
                    {
                        FileLogger.Log(
                            $"[DailyChallengeData] no valid challenge quest for "
                            + $"group={group.GroupIndex} slot={slot.SlotIndex}");
                        continue;
                    }

                    var selectedIndex = SelectStableCandidate(
                        characterId,
                        dayId,
                        group.GroupIndex,
                        slot.SlotIndex,
                        candidates.Count);
                    var selectedQuestId = candidates[selectedIndex];
                    generatedGroup.Entries.Add(new DailyChallengeGenerationEntry
                    {
                        EntryIndex = generatedGroup.Entries.Count,
                        QuestId = selectedQuestId,
                        TargetValue = QuestData.GetInitTrigger(selectedQuestId),
                    });
                }

                if (generatedGroup.Entries.Count > 0)
                    plan.Groups.Add(generatedGroup);
            }

            return plan;
        }

        internal static bool IsSuitableDungeonClearSpecialChallenge(
            int challengeType) => challengeType == 12;

        private static DailyChallengeSpecialDefinition ResolveSpecialChallenge(
            int characterLevel)
        {
            foreach (var candidate in Catalog.Value.SpecialChallenges)
            {
                if (characterLevel < candidate.MinimumLevel
                    || characterLevel > candidate.MaximumLevel)
                {
                    continue;
                }

                // The A14 client identifies special challenges by protocol type.
                // Type 12 is the PVF entry rendered as
                // "clear 5 recommended-level dungeons". Keep the mapping at the
                // protocol boundary; dungeon eligibility itself remains data-driven.
                if (candidate.ChallengeType == 12)
                {
                    return new DailyChallengeSpecialDefinition
                    {
                        ChallengeType = candidate.ChallengeType,
                        MinimumLevel = candidate.MinimumLevel,
                        MaximumLevel = candidate.MaximumLevel,
                        TargetValue = 5,
                    };
                }

                // The table contains other client-defined challenge kinds. Do not
                // assign them the clear-dungeon semantics until their authoritative
                // server events are implemented.
                return null;
            }

            return null;
        }

        internal static bool TryResolveReward(
            int groupIndex,
            int characterLevel,
            int activeEntryCount,
            out DailyChallengeRewardDefinition reward)
        {
            reward = null;
            if (groupIndex < 0 || groupIndex >= Catalog.Value.Groups.Count)
                return false;

            var group = Catalog.Value.Groups[groupIndex];
            if (characterLevel < group.MinimumLevel || characterLevel > group.MaximumLevel)
                return false;

            DailyChallengeLevelReward levelReward = null;
            foreach (var candidate in group.Rewards)
            {
                if (characterLevel >= candidate.MinimumLevel
                    && characterLevel <= candidate.MaximumLevel)
                {
                    levelReward = candidate;
                    break;
                }
            }

            if (levelReward == null || levelReward.ItemId <= 0 || levelReward.ItemCount <= 0)
                return false;

            var required = group.RequiredCompletionCount;
            if (required <= 0)
                required = group.ResolveActiveSlotCount(characterLevel);
            if (required <= 0)
                required = activeEntryCount;
            if (required <= 0)
                return false;

            reward = new DailyChallengeRewardDefinition
            {
                GroupIndex = groupIndex,
                RequiredCompletionCount = required,
                ItemId = levelReward.ItemId,
                ItemCount = levelReward.ItemCount,
            };
            return true;
        }

        private static DailyChallengeCatalog LoadCatalog()
        {
            var catalog = new DailyChallengeCatalog();
            try
            {
                var text = PvfArchiveAccessor.ReadText(ConfigPath);
                var root = new ScriptParser().Parse(text);
                var groupIndex = 0;
                foreach (var node in root.GetChildren("group"))
                {
                    var group = new DailyChallengeGroupDefinition
                    {
                        GroupIndex = groupIndex++,
                    };

                    var levels = ParseInts(node.GetChild("level")?.GetFirstDataContent(text));
                    if (levels.Count >= 2)
                    {
                        group.MinimumLevel = levels[0];
                        group.MaximumLevel = levels[1];
                    }

                    var required = ParseInts(
                        node.GetChild("reward challenge num")?.GetFirstDataContent(text));
                    if (required.Count > 0)
                        group.RequiredCompletionCount = required[0];

                    var slotCounts = ParseInts(
                        node.GetChild("slot num table")?.GetFirstDataContent(text));
                    for (var index = 0; index + 2 < slotCounts.Count; index += 3)
                    {
                        group.SlotCounts.Add(new DailyChallengeSlotCount
                        {
                            MinimumLevel = slotCounts[index],
                            MaximumLevel = slotCounts[index + 1],
                            Count = slotCounts[index + 2],
                        });
                    }

                    foreach (var slot in node.GetChildren("slot"))
                    {
                        var values = ParseInts(slot.GetFirstDataContent(text));
                        if (values.Count <= 1)
                            continue;

                        var slotDefinition = new DailyChallengeSlotDefinition
                        {
                            SlotIndex = values[0],
                        };
                        for (var index = 1; index < values.Count; index++)
                        {
                            if (values[index] > 0)
                            {
                                catalog.QuestIds.Add(values[index]);
                                slotDefinition.QuestIds.Add(values[index]);
                            }
                        }
                        group.Slots.Add(slotDefinition);
                    }

                    var rewards = ParseInts(
                        node.GetChild("reward table")?.GetFirstDataContent(text));
                    for (var index = 0; index + 3 < rewards.Count; index += 4)
                    {
                        group.Rewards.Add(new DailyChallengeLevelReward
                        {
                            MinimumLevel = rewards[index],
                            MaximumLevel = rewards[index + 1],
                            ItemId = rewards[index + 2],
                            ItemCount = rewards[index + 3],
                        });
                    }

                    catalog.Groups.Add(group);
                }

                var specialValues = ParseInts(
                    root.GetChild("special challenge")?.GetFirstDataContent(text));
                for (var index = 0; index + 2 < specialValues.Count; index += 3)
                {
                    catalog.SpecialChallenges.Add(new DailyChallengeSpecialDefinition
                    {
                        ChallengeType = specialValues[index],
                        MinimumLevel = specialValues[index + 1],
                        MaximumLevel = specialValues[index + 2],
                    });
                }

                FileLogger.Log(
                    $"[DailyChallengeData] groups={catalog.Groups.Count} "
                    + $"quests={catalog.QuestIds.Count} "
                    + $"special={catalog.SpecialChallenges.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DailyChallengeData] failed to load {ConfigPath}: {ex.Message}");
            }

            return catalog;
        }

        private static int SelectStableCandidate(
            int characterId,
            int dayId,
            int groupIndex,
            int slotIndex,
            int candidateCount)
        {
            unchecked
            {
                uint hash = 2166136261;
                hash = (hash ^ (uint)characterId) * 16777619;
                hash = (hash ^ (uint)dayId) * 16777619;
                hash = (hash ^ (uint)groupIndex) * 16777619;
                hash = (hash ^ (uint)slotIndex) * 16777619;
                return (int)(hash % (uint)candidateCount);
            }
        }

        private static List<int> ParseInts(string data)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(data))
                return result;

            foreach (var token in data.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out var value))
                    result.Add(value);
            }

            return result;
        }

        private sealed class DailyChallengeCatalog
        {
            internal List<DailyChallengeGroupDefinition> Groups { get; } =
                new List<DailyChallengeGroupDefinition>();

            internal HashSet<int> QuestIds { get; } = new HashSet<int>();

            internal List<DailyChallengeSpecialDefinition> SpecialChallenges { get; } =
                new List<DailyChallengeSpecialDefinition>();
        }

        private sealed class DailyChallengeGroupDefinition
        {
            internal int GroupIndex { get; set; }
            internal int MinimumLevel { get; set; }
            internal int MaximumLevel { get; set; } = int.MaxValue;
            internal int RequiredCompletionCount { get; set; }
            internal List<DailyChallengeSlotCount> SlotCounts { get; } =
                new List<DailyChallengeSlotCount>();
            internal List<DailyChallengeSlotDefinition> Slots { get; } =
                new List<DailyChallengeSlotDefinition>();
            internal List<DailyChallengeLevelReward> Rewards { get; } =
                new List<DailyChallengeLevelReward>();

            internal int ResolveActiveSlotCount(int level)
            {
                foreach (var entry in SlotCounts)
                {
                    if (level >= entry.MinimumLevel && level <= entry.MaximumLevel)
                        return entry.Count;
                }

                return 0;
            }
        }

        private sealed class DailyChallengeSlotDefinition
        {
            internal int SlotIndex { get; set; }
            internal List<int> QuestIds { get; } = new List<int>();
        }

        private sealed class DailyChallengeSlotCount
        {
            internal int MinimumLevel { get; set; }
            internal int MaximumLevel { get; set; }
            internal int Count { get; set; }
        }

        private sealed class DailyChallengeLevelReward
        {
            internal int MinimumLevel { get; set; }
            internal int MaximumLevel { get; set; }
            internal int ItemId { get; set; }
            internal int ItemCount { get; set; }
        }
    }
}
