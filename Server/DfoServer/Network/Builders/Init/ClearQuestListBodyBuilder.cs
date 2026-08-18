using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public sealed class ClearQuestListBodyBuilder : IInitPacketBuilder
    {
        internal const int PayloadLength = 30000;

        public ushort NotiType => 0x0164;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var clearedFlags = new Dictionary<int, int>();
            foreach (var entry in init.CharacInvisibleFalgs)
                clearedFlags[entry.SlotIndex] = entry.FlagValue;
            body = BuildBody(clearedFlags);
            return true;
        }

        internal static byte[] BuildBody(
            IReadOnlyDictionary<int, int> clearedFlags)
        {
            var body = new byte[4 + PayloadLength];
            Buffer.BlockCopy(
                BitConverter.GetBytes(PayloadLength),
                0,
                body,
                0,
                4);
            if (clearedFlags == null)
                return body;

            foreach (var entry in clearedFlags)
            {
                if (entry.Key >= 0 && entry.Key < PayloadLength)
                    body[4 + entry.Key] = (byte)entry.Value;
            }
            return body;
        }
    }
}
