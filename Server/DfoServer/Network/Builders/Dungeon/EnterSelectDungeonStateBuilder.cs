using DfoServer.Game.Session;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public static class EnterSelectDungeonStateBuilder
    {
        public static byte[] BuildUserState(PlayerContext player)
            => BuildUserState(new[] { player.UserId }, player.UserState);

        public static byte[] BuildUserState(
            IReadOnlyList<ushort> userIds,
            byte userState)
        {
            var writer = new GamePacketWriter();
            var count = userIds?.Count ?? 0;

            writer.WriteByte((byte)count);
            for (var i = 0; i < count; i++)
            {
                writer.WriteUInt16(userIds[i]);
                writer.WriteByte(userState);
            }
            return writer.ToArray();
        }

        public static byte[] BuildEnterSelectDungeon(
            PlayerContext player,
            int towerOfDespairFloor)
            => BuildEnterSelectDungeon(
                new[] { player.UserId },
                towerOfDespairFloor,
                hellQuestBlockedSlots: null);

        public static byte[] BuildEnterSelectDungeon(
            IReadOnlyList<ushort> userIds,
            int towerOfDespairFloor)
            => BuildEnterSelectDungeon(
                userIds,
                towerOfDespairFloor,
                hellQuestBlockedSlots: null);

        public static byte[] BuildEnterSelectDungeon(
            IReadOnlyList<ushort> userIds,
            int towerOfDespairFloor,
            IReadOnlyList<ushort> hellQuestBlockedSlots)
        {
            var writer = new GamePacketWriter();
            var count = userIds?.Count ?? 0;
            var blockedCount = hellQuestBlockedSlots?.Count ?? 0;

            writer.WriteInt32(0x01);
            // The original server writes the number of party slots that have
            // not cleared this world-map area's [hell quest], followed by the
            // blocked slot indexes. The client uses this list to disable the
            // "challenge hell party" button before SELECT_DUNGEON is sent.
            writer.WriteByte((byte)blockedCount);
            for (var i = 0; i < blockedCount; i++)
                writer.WriteUInt16(hellQuestBlockedSlots[i]);

            // Reserved byte present in the JP86 0x001B layout.
            writer.WriteByte(0x00);
            writer.WriteByte((byte)count);
            for (var i = 0; i < count; i++)
            {
                writer.WriteUInt16(userIds[i]);
                writer.WriteByte(0x00);
            }
            writer.WriteInt32(0x00);
            // For a solo entry the client reads this u16 at body offset 14.
            // A party entry naturally moves it by three bytes per extra member.
            writer.WriteUInt16((ushort)towerOfDespairFloor);
            writer.WriteZeroBytes(3);
            return writer.ToArray();
        }
    }
}
