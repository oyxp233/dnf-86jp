using DfoServer.Game.Party;
using DfoServer.Game.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// Handles the legacy 8.6 SEND_MESSAGE request.  The client sends
    /// mode:u8 + targetUid:u16 + targetCharacterId:u32 + message:dstr and,
    /// for direct-message modes, may append targetName:dstr.
    /// </summary>
    public sealed class ChatHandler
    {
        private const int MaximumMessageBytes = 256;
        private const byte DirectMessageMode = 1;
        private const byte PartyMessageMode = 2;
        private const byte AreaMessageMode = 3;
        private const byte AlternateDirectMessageMode = 7;

        private readonly ISessionDirectory _sessions;
        private readonly PartyManager _parties;

        public ChatHandler(
            ISessionDirectory sessions,
            PartyManager parties)
        {
            _sessions = sessions
                ?? throw new ArgumentNullException(nameof(sessions));
            _parties = parties
                ?? throw new ArgumentNullException(nameof(parties));
        }

        public async Task Handle_SEND_MESSAGE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || !TryParseRequest(body, out var request))
            {
                FileLogger.Log(
                    $"[GameProtocol] SEND_MESSAGE invalid " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"body({body?.Length ?? 0}B): " +
                    $"{(body == null ? "null" : BitConverter.ToString(body))}");
                return;
            }

            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.MESSAGE,
                BuildNotificationBody(
                    request.Mode,
                    session.Player.UserId,
                    serverGroup: 0,
                    request.MessageBytes));

            var recipients = ResolveRecipients(session, request);
            var sendTasks = new List<Task>(recipients.Count);
            foreach (var recipient in recipients)
                sendTasks.Add(recipient.SendPacketAsync(packet));

            if (sendTasks.Count > 0)
                await Task.WhenAll(sendTasks);

            FileLogger.Log(
                $"[GameProtocol] SEND_MESSAGE cid={session.Player.CharacterId} " +
                $"uid={session.Player.UserId} mode={request.Mode} " +
                $"targetUid={request.TargetUniqueId} " +
                $"targetCid={request.TargetCharacterId} " +
                $"messageBytes={request.MessageBytes.Length} " +
                $"recipients={recipients.Count}");
        }

        internal IReadOnlyList<EnhancedClientSession> ResolveRecipients(
            EnhancedClientSession sender,
            ChatMessageRequest request)
        {
            var result = new Dictionary<Guid, EnhancedClientSession>();
            AddIfCurrentChannel(result, sender, sender);

            if (request.Mode == DirectMessageMode
                || request.Mode == AlternateDirectMessageMode)
            {
                AddIfCurrentChannel(
                    result,
                    sender,
                    FindDirectTarget(request));
                return result.Values.ToList();
            }

            if (request.Mode == PartyMessageMode
                || sender.Player.CurrentRun != null)
            {
                var party = _parties.GetPartyByUser(sender.Player.UserId);
                if (party != null)
                {
                    foreach (var member in party.MembersBySlot())
                    {
                        if (_sessions.TryGet(
                                member.CharacterId,
                                out var memberSession))
                        {
                            AddIfCurrentChannel(
                                result,
                                sender,
                                memberSession);
                        }
                    }
                }
                return result.Values.ToList();
            }

            if (request.Mode == AreaMessageMode)
            {
                foreach (var areaSession in _sessions.GetSessionsInArea(
                             sender.Player.CurTownId,
                             sender.Player.CurAreaId,
                             sender.Player.CharacterId,
                             sender.ListenerPort))
                {
                    AddIfCurrentChannel(result, sender, areaSession);
                }
            }

            // Unknown modes deliberately remain sender-only.  Several values
            // are backed by guild/megaphone services and must not become a
            // free cross-channel broadcast merely because their wire shape is
            // shared with ordinary chat.
            return result.Values.ToList();
        }

        private EnhancedClientSession FindDirectTarget(
            ChatMessageRequest request)
        {
            if (request.TargetCharacterId > 0
                && request.TargetCharacterId <= int.MaxValue
                && _sessions.TryGet(
                    (int)request.TargetCharacterId,
                    out var byCharacterId))
            {
                return byCharacterId;
            }

            foreach (var candidate in _sessions.GetAllGameSessions())
            {
                if (candidate?.Player == null)
                    continue;
                if (request.TargetUniqueId != 0
                    && candidate.Player.UserId == request.TargetUniqueId)
                {
                    return candidate;
                }
                if (request.TargetNameBytes.Length > 0
                    && candidate.Player.Name != null
                    && candidate.Player.Name.SequenceEqual(
                        request.TargetNameBytes))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static void AddIfCurrentChannel(
            IDictionary<Guid, EnhancedClientSession> recipients,
            EnhancedClientSession sender,
            EnhancedClientSession candidate)
        {
            if (candidate?.Player == null
                || candidate.Player.CharacterId <= 0
                || candidate.TcpClient == null
                || !candidate.TcpClient.Connected)
            {
                return;
            }
            if (sender.ListenerPort > 0
                && candidate.ListenerPort != sender.ListenerPort)
            {
                return;
            }
            recipients[candidate.SessionId] = candidate;
        }

        internal static bool TryParseRequest(
            byte[] body,
            out ChatMessageRequest request)
        {
            request = null;
            if (body == null || body.Length < 11)
                return false;

            var mode = body[0];
            var targetUniqueId = BitConverter.ToUInt16(body, 1);
            var targetCharacterId = BitConverter.ToUInt32(body, 3);
            var messageLength = BitConverter.ToInt32(body, 7);
            if (messageLength <= 0
                || messageLength > MaximumMessageBytes
                || body.Length < 11 + messageLength)
            {
                return false;
            }

            var messageBytes = new byte[messageLength];
            Buffer.BlockCopy(body, 11, messageBytes, 0, messageLength);
            if (Array.IndexOf(messageBytes, (byte)0) >= 0)
                return false;

            var offset = 11 + messageLength;
            var targetNameBytes = Array.Empty<byte>();
            if (mode == DirectMessageMode
                || mode == AlternateDirectMessageMode)
            {
                if (body.Length > offset)
                {
                    if (body.Length < offset + 4)
                        return false;
                    var nameLength = BitConverter.ToInt32(body, offset);
                    if (nameLength < 0
                        || nameLength > 30
                        // 86JP appends a one-byte direct-conversation flag
                        // after targetName. Older clients omit it.
                        || (body.Length != offset + 4 + nameLength
                            && body.Length != offset + 5 + nameLength))
                    {
                        return false;
                    }
                    targetNameBytes = new byte[nameLength];
                    if (nameLength > 0)
                    {
                        Buffer.BlockCopy(
                            body,
                            offset + 4,
                            targetNameBytes,
                            0,
                            nameLength);
                    }
                }
            }
            else if (body.Length != offset)
            {
                return false;
            }

            request = new ChatMessageRequest(
                mode,
                targetUniqueId,
                targetCharacterId,
                messageBytes,
                targetNameBytes);
            return true;
        }

        internal static byte[] BuildNotificationBody(
            byte mode,
            ushort senderUniqueId,
            byte serverGroup,
            byte[] messageBytes)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteUInt16(senderUniqueId);
            writer.WriteByte(serverGroup);
            writer.WriteDstr(messageBytes ?? Array.Empty<byte>());
            return writer.ToArray();
        }
    }

    internal sealed class ChatMessageRequest
    {
        internal ChatMessageRequest(
            byte mode,
            ushort targetUniqueId,
            uint targetCharacterId,
            byte[] messageBytes,
            byte[] targetNameBytes)
        {
            Mode = mode;
            TargetUniqueId = targetUniqueId;
            TargetCharacterId = targetCharacterId;
            MessageBytes = messageBytes ?? Array.Empty<byte>();
            TargetNameBytes = targetNameBytes ?? Array.Empty<byte>();
        }

        internal byte Mode { get; }
        internal ushort TargetUniqueId { get; }
        internal uint TargetCharacterId { get; }
        internal byte[] MessageBytes { get; }
        internal byte[] TargetNameBytes { get; }
    }
}
