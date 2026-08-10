using DfoServer.Game.Characters;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class ChatBroadcastSelfTest
    {
        private const int ChannelOnePort = 10011;
        private const int ChannelTwoPort = 10012;

        public static int Run()
        {
            var failures = 0;
            VerifyRequestParsing(ref failures);
            VerifyNotificationBody(ref failures);
            VerifyRecipientRouting(ref failures);

            Console.WriteLine(
                $"=== CHAT_BROADCAST result: failures={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyRequestParsing(ref int failures)
        {
            var areaBody = BuildRequestBody(
                mode: 3,
                targetUniqueId: 0,
                targetCharacterId: 0,
                message: new byte[] { (byte)'h', (byte)'i' });
            var areaParsed = ChatHandler.TryParseRequest(
                areaBody,
                out var areaRequest);
            Check(
                "area request parses the legacy SEND_MESSAGE fields",
                areaParsed
                && areaRequest.Mode == 3
                && areaRequest.TargetUniqueId == 0
                && areaRequest.TargetCharacterId == 0
                && areaRequest.MessageBytes.SequenceEqual(
                    new byte[] { (byte)'h', (byte)'i' })
                && areaRequest.TargetNameBytes.Length == 0,
                ref failures);

            // Captured from the 86JP client:
            // mode=1, targetUid=2, targetCid=0, message="111",
            // targetName="test1", trailing direct-conversation flag=1.
            var whisperBody = new byte[]
            {
                0x01,
                0x02, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x03, 0x00, 0x00, 0x00,
                0x31, 0x31, 0x31,
                0x05, 0x00, 0x00, 0x00,
                0x74, 0x65, 0x73, 0x74, 0x31,
                0x01,
            };
            var whisperParsed = ChatHandler.TryParseRequest(
                whisperBody,
                out var whisperRequest);
            Check(
                "whisper request preserves target identity and raw text bytes",
                whisperParsed
                && whisperRequest.Mode == 1
                && whisperRequest.TargetUniqueId == 2
                && whisperRequest.TargetCharacterId == 0
                && whisperRequest.MessageBytes.SequenceEqual(
                    new byte[] { 0x31, 0x31, 0x31 })
                && whisperRequest.TargetNameBytes.SequenceEqual(
                    new byte[] { 0x74, 0x65, 0x73, 0x74, 0x31 }),
                ref failures);

            Check(
                "whisper request rejects more than one trailing flag byte",
                !ChatHandler.TryParseRequest(
                    whisperBody.Concat(new byte[] { 0x00 }).ToArray(),
                    out _),
                ref failures);

            var legacyWhisperBody = BuildRequestBody(
                mode: 1,
                targetUniqueId: 1002,
                targetCharacterId: 0,
                message: new byte[] { (byte)'o', (byte)'k' },
                targetName: new byte[] { (byte)'p', (byte)'e', (byte)'e', (byte)'r' });
            Check(
                "legacy whisper request without trailing flag remains supported",
                ChatHandler.TryParseRequest(legacyWhisperBody, out _),
                ref failures);

            var truncated = areaBody.Take(areaBody.Length - 1).ToArray();
            Check(
                "truncated message length fails closed",
                !ChatHandler.TryParseRequest(truncated, out _),
                ref failures);

            var embeddedNull = BuildRequestBody(
                mode: 2,
                targetUniqueId: 0,
                targetCharacterId: 0,
                message: new byte[] { (byte)'a', 0, (byte)'b' });
            Check(
                "embedded NUL message fails closed",
                !ChatHandler.TryParseRequest(embeddedNull, out _),
                ref failures);

            var oversized = BuildRequestBody(
                mode: 3,
                targetUniqueId: 0,
                targetCharacterId: 0,
                message: new byte[257]);
            Check(
                "messages beyond the 256-byte limit fail closed",
                !ChatHandler.TryParseRequest(oversized, out _),
                ref failures);
        }

        private static void VerifyNotificationBody(ref int failures)
        {
            var body = ChatHandler.BuildNotificationBody(
                mode: 2,
                senderUniqueId: 0x1234,
                serverGroup: 0,
                messageBytes: new byte[] { 0x41, 0x42 });
            Check(
                "notification body uses mode, sender uid, group and dstr",
                body.SequenceEqual(
                    new byte[]
                    {
                        0x02,
                        0x34, 0x12,
                        0x00,
                        0x02, 0x00, 0x00, 0x00,
                        0x41, 0x42,
                    }),
                ref failures);
        }

        private static void VerifyRecipientRouting(ref int failures)
        {
            var sessions = new SessionDirectory();
            var parties = new PartyManager();
            using var sender = ConnectedSession.Create(
                1001, "sender", ChannelOnePort, townId: 1, areaId: 2);
            using var nearby = ConnectedSession.Create(
                1002, "nearby", ChannelOnePort, townId: 1, areaId: 2);
            using var otherArea = ConnectedSession.Create(
                1003, "other-area", ChannelOnePort, townId: 1, areaId: 3);
            using var otherChannel = ConnectedSession.Create(
                1004, "other-channel", ChannelTwoPort, townId: 1, areaId: 2);
            using var partyPeer = ConnectedSession.Create(
                1005, "party-peer", ChannelOnePort, townId: 2, areaId: 1);

            foreach (var fixture in new[]
                     {
                         sender,
                         nearby,
                         otherArea,
                         otherChannel,
                         partyPeer,
                     })
            {
                sessions.Register(
                    fixture.Session.Player.CharacterId,
                    fixture.Session);
            }

            var party = parties.CreateParty(ToPartyMember(sender)).Party;
            parties.Join(party.PartyId, ToPartyMember(partyPeer));
            var handler = new ChatHandler(sessions, parties);

            var areaRecipients = handler.ResolveRecipients(
                sender.Session,
                Request(mode: 3));
            Check(
                "nearby chat reaches sender and same-area same-channel peers only",
                HasExactly(areaRecipients, sender, nearby),
                ref failures);

            var partyRecipients = handler.ResolveRecipients(
                sender.Session,
                Request(mode: 2));
            Check(
                "party chat reaches party members across town areas",
                HasExactly(partyRecipients, sender, partyPeer),
                ref failures);

            var whisperRecipients = handler.ResolveRecipients(
                sender.Session,
                Request(
                    mode: 1,
                    targetUniqueId: 1003,
                    targetCharacterId: 1003));
            Check(
                "whisper reaches its online target outside the sender area",
                HasExactly(whisperRecipients, sender, otherArea),
                ref failures);

            var crossChannelWhisper = handler.ResolveRecipients(
                sender.Session,
                Request(
                    mode: 1,
                    targetUniqueId: 1004,
                    targetCharacterId: 1004));
            Check(
                "whisper does not leak to another game channel",
                HasExactly(crossChannelWhisper, sender),
                ref failures);

            var nameFallbackWhisper = handler.ResolveRecipients(
                sender.Session,
                Request(
                    mode: 1,
                    targetName: otherArea.Session.Player.Name));
            Check(
                "whisper can resolve the target by raw character name",
                HasExactly(nameFallbackWhisper, sender, otherArea),
                ref failures);

            var unknownMode = handler.ResolveRecipients(
                sender.Session,
                Request(mode: 99));
            Check(
                "unknown chat modes fail closed to sender-only",
                HasExactly(unknownMode, sender),
                ref failures);
        }

        private static ChatMessageRequest Request(
            byte mode,
            ushort targetUniqueId = 0,
            uint targetCharacterId = 0,
            byte[] targetName = null)
        {
            return new ChatMessageRequest(
                mode,
                targetUniqueId,
                targetCharacterId,
                new byte[] { (byte)'o', (byte)'k' },
                targetName ?? Array.Empty<byte>());
        }

        private static byte[] BuildRequestBody(
            byte mode,
            ushort targetUniqueId,
            uint targetCharacterId,
            byte[] message,
            byte[] targetName = null)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteUInt16(targetUniqueId);
            writer.WriteUInt32(targetCharacterId);
            writer.WriteDstr(message);
            if (targetName != null)
                writer.WriteDstr(targetName);
            return writer.ToArray();
        }

        private static PartyMember ToPartyMember(ConnectedSession fixture)
        {
            return new PartyMember
            {
                UserId = fixture.Session.Player.UserId,
                CharacterId = fixture.Session.Player.CharacterId,
                SessionId = fixture.Session.SessionId,
                Name = fixture.Name,
            };
        }

        private static bool HasExactly(
            IReadOnlyList<EnhancedClientSession> actual,
            params ConnectedSession[] expected)
        {
            var actualIds = new HashSet<Guid>(
                actual.Select(session => session.SessionId));
            var expectedIds = new HashSet<Guid>(
                expected.Select(fixture => fixture.Session.SessionId));
            return actualIds.SetEquals(expectedIds);
        }

        private static void Check(
            string name,
            bool passed,
            ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                failures++;
        }

        private sealed class ConnectedSession : IDisposable
        {
            private readonly TcpClient _serverSide;

            private ConnectedSession(
                string name,
                EnhancedClientSession session,
                TcpClient serverSide)
            {
                Name = name;
                Session = session;
                _serverSide = serverSide;
            }

            internal string Name { get; }
            internal EnhancedClientSession Session { get; }

            internal static ConnectedSession Create(
                int characterId,
                string name,
                int listenerPort,
                byte townId,
                byte areaId)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var client = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                client.Connect(endpoint.Address, endpoint.Port);
                var server = accept.GetAwaiter().GetResult();
                listener.Stop();

                var session = new EnhancedClientSession(
                    client,
                    new GamePacketHeader(),
                    listenerPort);
                session.Player.HydrateIdentityFrom(
                    new CharacterRecord
                    {
                        CharacterId = characterId,
                        Name = System.Text.Encoding.ASCII.GetBytes(name),
                        Level = 1,
                        UserState = 0,
                    });
                session.Player.CurTownId = townId;
                session.Player.CurAreaId = areaId;
                session.Player.TownPresenceReady = true;

                return new ConnectedSession(name, session, server);
            }

            public void Dispose()
            {
                Session.Close();
                _serverSide.Close();
            }
        }
    }
}
