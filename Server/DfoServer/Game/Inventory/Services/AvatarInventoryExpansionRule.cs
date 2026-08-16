using System;

namespace DfoServer.Game.Inventory
{
    internal static class AvatarInventoryExpansionRule
    {
        internal const int FirstItemTemplateId = 2683901;
        internal const int StageCount = 15;
        internal const ushort SlotsPerStage = 7;
        internal const ushort BaseCapacity = 105;
        internal const ushort MaxExpansion = StageCount * SlotsPerStage;
        internal const ushort MaxCapacity = BaseCapacity + MaxExpansion;

        internal static bool TryResolveTargetExpansion(int itemTemplateId, out ushort targetExpansion)
        {
            targetExpansion = 0;
            var stageIndex = itemTemplateId - FirstItemTemplateId;
            if (stageIndex < 0 || stageIndex >= StageCount)
                return false;

            targetExpansion = (ushort)((stageIndex + 1) * SlotsPerStage);
            return true;
        }

        internal static bool CanApply(ushort currentExpansion, ushort targetExpansion)
        {
            return IsValidExpansion(currentExpansion)
                && IsValidExpansion(targetExpansion)
                && targetExpansion > 0
                && currentExpansion + SlotsPerStage == targetExpansion;
        }

        internal static bool IsValidExpansion(ushort expansion)
        {
            return expansion <= MaxExpansion && expansion % SlotsPerStage == 0;
        }

        internal static ushort NormalizeExpansion(ushort expansion)
        {
            var clamped = Math.Min((int)expansion, MaxExpansion);
            return (ushort)(clamped - clamped % SlotsPerStage);
        }

        internal static ushort GetOpenCapacity(int expansion)
        {
            var normalized = NormalizeExpansion(ToUInt16Clamped(expansion));
            return (ushort)(BaseCapacity + normalized);
        }

        private static ushort ToUInt16Clamped(int value)
        {
            if (value <= 0)
                return 0;
            if (value >= ushort.MaxValue)
                return ushort.MaxValue;
            return (ushort)value;
        }
    }
}
