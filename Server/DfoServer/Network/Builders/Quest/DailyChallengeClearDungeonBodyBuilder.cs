using System;

namespace DfoServer.Network.Builders
{
    internal static class DailyChallengeClearDungeonBodyBuilder
    {
        // 0x0287 carries one opaque completion token.  The A14 client inserts
        // the uint32 into a deduplicated vector and renders that vector's length
        // as special-challenge progress; it does not validate a dungeon index.
        internal static byte[] Build(uint completionToken) =>
            BitConverter.GetBytes(completionToken);
    }
}
