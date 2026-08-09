using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Handlers.Pets;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// Owns character-session transitions that must remain generation-safe.
    /// Protocol routing delegates here instead of coordinating shared state.
    /// </summary>
    internal sealed class CharacterSessionLifecycleCoordinator
    {
        private const string ProtocolName = "GameProtocol";

        private readonly LoginHandler _loginHandler;
        private readonly CharacterSelectHandler _characterSelectHandler;
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSelectCharacterDataSource _selectCharacterDataSource;
        private readonly ISessionDirectory _sessionDirectory;
        private readonly CharacterTransitionCoordinator _characterTransitions;
        private readonly ExpertJobStoreHandler _expertJobStoreHandler;
        private readonly TownHandler _townHandler;
        private readonly DungeonInstanceRegistry _dungeonInstances;
        private readonly DungeonRejoinCoordinator _dungeonRejoin;
        private readonly LotteryItemHandler _lotteryItemHandler;
        private readonly CraneMiniGameHandler _craneMiniGameHandler;
        private readonly PvpRoomHandler _pvpRoomHandler;
        private readonly InventoryRefreshSender _inventoryRefreshSender;

        internal CharacterSessionLifecycleCoordinator(
            LoginHandler loginHandler,
            CharacterSelectHandler characterSelectHandler,
            ICharacterRepository characterRepository,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            ISessionDirectory sessionDirectory,
            CharacterTransitionCoordinator characterTransitions,
            ExpertJobStoreHandler expertJobStoreHandler,
            TownHandler townHandler,
            DungeonInstanceRegistry dungeonInstances,
            DungeonRejoinCoordinator dungeonRejoin,
            LotteryItemHandler lotteryItemHandler,
            CraneMiniGameHandler craneMiniGameHandler,
            PvpRoomHandler pvpRoomHandler,
            InventoryRefreshSender inventoryRefreshSender)
        {
            _loginHandler = loginHandler;
            _characterSelectHandler = characterSelectHandler;
            _characterRepository = characterRepository;
            _selectCharacterDataSource = selectCharacterDataSource;
            _sessionDirectory = sessionDirectory;
            _characterTransitions = characterTransitions;
            _expertJobStoreHandler = expertJobStoreHandler;
            _townHandler = townHandler;
            _dungeonInstances = dungeonInstances;
            _dungeonRejoin = dungeonRejoin;
            _lotteryItemHandler = lotteryItemHandler;
            _craneMiniGameHandler = craneMiniGameHandler;
            _pvpRoomHandler = pvpRoomHandler;
            _inventoryRefreshSender = inventoryRefreshSender;
        }

        internal async Task HandleConnectedAsync(
            EnhancedClientSession session)
        {
            FileLogger.Log(
                $"[{ProtocolName}] Admin client connected: " +
                $"{session.SessionId}");
            PetCreatureRuntimeService.RegisterSession(session);
            await _loginHandler.Handle_ClientFirstConnected(session);
        }

        internal async Task HandleDisconnectedAsync(
            EnhancedClientSession session)
        {
            FileLogger.Log(
                $"[{ProtocolName}] Admin client disconnected: " +
                $"{session.SessionId}");
            var characterId = session.Player?.CharacterId ?? 0;
            var ownsGeneration = characterId <= 0;

            try
            {
                try
                {
                    await _expertJobStoreHandler.CloseSessionAsync(
                        session,
                        includeOwner: false);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] disconnect expert cleanup " +
                        $"failed cid={characterId}: {ex}");
                }

                if (characterId > 0)
                {
                    using (await _characterTransitions.AcquireAsync(
                               characterId))
                    {
                        try
                        {
                            ownsGeneration = await _sessionDirectory
                                .UnregisterAsync(characterId, session);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log(
                                $"[{ProtocolName}] disconnect unregister " +
                                $"failed cid={characterId}: {ex}");

                            // SessionDirectory removes before notifying its
                            // isolated subscribers. If an unexpected exception
                            // escaped after removal, this generation still owns
                            // the remaining shared teardown.
                            if (!_sessionDirectory.TryGet(
                                    characterId,
                                    out var remaining))
                            {
                                ownsGeneration = true;
                            }
                            else if (ReferenceEquals(remaining, session))
                            {
                                try
                                {
                                    ownsGeneration = await _sessionDirectory
                                        .UnregisterAsync(
                                            characterId,
                                            session);
                                }
                                catch (Exception retryEx)
                                {
                                    FileLogger.Log(
                                        $"[{ProtocolName}] disconnect " +
                                        $"unregister retry failed " +
                                        $"cid={characterId}: {retryEx}");
                                }
                            }
                        }

                        if (ownsGeneration)
                        {
                            try
                            {
                                await _townHandler.NotifyLeaveAsync(session);
                            }
                            catch (Exception ex)
                            {
                                FileLogger.Log(
                                    $"[{ProtocolName}] disconnect town " +
                                    $"cleanup failed cid={characterId}: {ex}");
                            }

                            try
                            {
                                var detachedForRejoin =
                                    DungeonRunLifecycle
                                        .DetachRunOnNetworkDisconnect(
                                            session,
                                            _dungeonInstances);
                                if (!detachedForRejoin)
                                {
                                    DungeonRunLifecycle.EndRunOnTeardown(
                                        session,
                                        "disconnect",
                                        _dungeonInstances);
                                }
                            }
                            catch (Exception ex)
                            {
                                FileLogger.Log(
                                    $"[{ProtocolName}] disconnect dungeon " +
                                    $"cleanup failed cid={characterId}: {ex}");
                            }

                            _townHandler.PersistPosition(
                                session,
                                forceImmediate: true,
                                source: "disconnect");
                        }
                    }

                    if (!ownsGeneration)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] Stale disconnect shared " +
                            $"cleanup skipped: cid={characterId} " +
                            $"session={session.SessionId}");
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] disconnect teardown orchestration " +
                    $"failed cid={characterId}: {ex}");
            }
            finally
            {
                if (characterId > 0)
                {
                    try
                    {
                        InventoryContext.Unregister(
                            session.SessionId,
                            characterId);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] disconnect inventory " +
                            $"cleanup failed cid={characterId}: {ex}");
                    }
                }

                try
                {
                    _dungeonRejoin.ClearSession(session.SessionId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] disconnect rejoin cleanup " +
                        $"failed session={session.SessionId}: {ex}");
                }

                try
                {
                    _lotteryItemHandler.ClearSession(session.SessionId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] disconnect lottery cleanup " +
                        $"failed session={session.SessionId}: {ex}");
                }

                try
                {
                    _craneMiniGameHandler.ClearSession(session.SessionId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] disconnect crane cleanup " +
                        $"failed session={session.SessionId}: {ex}");
                }

                try
                {
                    PetCreatureRuntimeService.UnregisterSession(session);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] disconnect pet cleanup failed " +
                        $"session={session.SessionId}: {ex}");
                }
            }
        }

        internal bool CanDispatch(
            EnhancedClientSession session,
            GamePacketHeader header)
        {
            if (OwnsRegisteredGeneration(_sessionDirectory, session))
                return true;

            FileLogger.Log(
                $"[{ProtocolName}] Packet rejected for stale session: " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"session={session?.SessionId} type=0x{header.type:X4}");
            return false;
        }

        internal async Task HandleSelectCharacterAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var selectedCharacter = ResolveSelectedCharacter(
                session,
                body,
                out var selectedSlot);
            if (selectedCharacter == null
                || selectedCharacter.CharacterId <= 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SELECT_CHARACTER could not resolve " +
                    $"an account character; closing " +
                    $"session={session.SessionId}");
                session.Close();
                return;
            }

            if (GameChannelAdmissionPolicy.TryGetCharacterEntryRejection(
                    session.ListenerPort,
                    selectedCharacter.Level,
                    out var admissionRejection))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SELECT_CHARACTER rejected by channel " +
                    $"admission: cid={selectedCharacter.CharacterId} " +
                    $"level={selectedCharacter.Level} " +
                    $"listener={session.ListenerPort} " +
                    $"minimum={GameChannelAdmissionPolicy.Channel100MinimumCharacterLevel}");
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(
                        0x01,
                        (ushort)CmdPacketType.SELECT_CHARACTER,
                        SelectCharacterAckBodyBuilder.BuildRejected(
                            admissionRejection.CommandErrorCode)));
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
                        ServerNoticeMessageBuilder.Build(
                            admissionRejection.Message)));
                return;
            }

            _dungeonRejoin.ClearSession(session.SessionId);
            session.PendingReturnSelectCharacterId = 0;
            var selectedCharacterId = selectedCharacter.CharacterId;

            var previousCharacterId = session.Player?.CharacterId ?? 0;
            if (previousCharacterId > 0)
            {
                if (!await LeaveCurrentCharacterForSelectionAsync(
                        session,
                        previousCharacterId))
                {
                    return;
                }
            }
            else
            {
                EnterCharacterSelectionState(session);
            }

            using (await _characterTransitions.AcquireAsync(
                       selectedCharacterId))
            {
                try
                {
                    var displaced = await _sessionDirectory
                        .RegisterReplacingAsync(
                            selectedCharacterId,
                            session);
                    if (displaced != null)
                    {
                        await CleanupDisplacedSessionAsync(
                            selectedCharacterId,
                            displaced);
                    }

                    await _characterSelectHandler
                        .HandleResolvedSelectCharacterAsync(
                            session,
                            selectedCharacter,
                            selectedSlot);
                    var prepared =
                        session.Player?.CharacterId == selectedCharacterId
                        && _sessionDirectory.TryGet(
                            selectedCharacterId,
                            out var current)
                        && ReferenceEquals(current, session);
                    if (!prepared)
                    {
                        throw new InvalidOperationException(
                            "selected character preparation did not publish " +
                            "the registered generation");
                    }

                    var gameSessionConnectionString =
                        SqliteDatabaseBootstrap.Initialize(
                            ServerPaths.DatabasePath,
                            ServerPaths.SchemaFilePath);
                    session.GameSession = new Game.Session.GameSession(
                        session,
                        gameSessionConnectionString);
                    await _pvpRoomHandler.HandleLobbyReadyAsync(session);
                    await _inventoryRefreshSender
                        .SendAllEquipmentItemLockListRefresh(session);
                    await session.GameSession.QuestManager
                        .SyncItemSeekingQuestProgressAsync(null);
                    await PetCreatureRuntimeService.BeginTownAsync(
                        session,
                        "select_character");
                    await _dungeonRejoin.ProjectCandidateAsync(session);
                }
                catch (Exception ex)
                {
                    await RollbackSelectedSessionAsync(
                        selectedCharacterId,
                        session);
                    FileLogger.Log(
                        $"[{ProtocolName}] SELECT_CHARACTER failed " +
                        $"cid={selectedCharacterId} " +
                        $"session={session.SessionId}: {ex}");
                    session.Close();
                }
            }
        }

        internal async Task HandleReturnSelectCharacterAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var characterId = session.Player?.CharacterId ?? 0;
            if (characterId <= 0)
            {
                session.PendingReturnSelectCharacterId = 0;
                await _characterSelectHandler
                    .Handle_ENUM_CMDPACKET_RETURN_SELECT_CHARACTER(
                        session,
                        header,
                        body);
                EnterCharacterSelectionState(session);
                return;
            }

            using (await _characterTransitions.AcquireAsync(characterId))
            {
                if (!_sessionDirectory.TryGet(
                        characterId,
                        out var current)
                    || !ReferenceEquals(current, session))
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] RETURN_SELECT rejected for stale " +
                        $"session: cid={characterId} " +
                        $"session={session.SessionId}");
                    return;
                }

                // Complete fallible shared-state cleanup while this exact
                // generation is still discoverable by disconnect teardown.
                await _expertJobStoreHandler.CloseSessionAsync(
                    session,
                    includeOwner: true);
                await _townHandler.NotifyLeaveAsync(session);
                _townHandler.PersistPosition(
                    session,
                    forceImmediate: true,
                    source: "return_select");
                DungeonRunLifecycle.EndRunOnTeardown(
                    session,
                    "return_select",
                    _dungeonInstances);
                if (!await _sessionDirectory.UnregisterAsync(
                        characterId,
                        session))
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] RETURN_SELECT generation changed " +
                        $"during cleanup: cid={characterId} " +
                        $"session={session.SessionId}");
                    return;
                }

                InventoryContext.Unregister(
                    session.SessionId,
                    characterId);
                session.GameSession = null;
                session.PendingReturnSelectCharacterId = characterId;
                await _characterSelectHandler
                    .Handle_ENUM_CMDPACKET_RETURN_SELECT_CHARACTER(
                        session,
                        header,
                        body);
                EnterCharacterSelectionState(session);
            }
        }

        internal static bool OwnsRegisteredGeneration(
            ISessionDirectory sessions,
            EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0)
                return true;

            return sessions != null
                && sessions.TryGet(characterId, out var current)
                && ReferenceEquals(current, session);
        }

        internal static void EnterCharacterSelectionState(
            EnhancedClientSession session)
        {
            if (session?.Player == null)
                return;

            session.Player.CharacterId = 0;
            session.Player.UserId = 0;
        }

        private CharacterRecord ResolveSelectedCharacter(
            EnhancedClientSession session,
            byte[] body,
            out int selectedSlot)
        {
            var slot = body != null && body.Length >= 2
                ? BitConverter.ToUInt16(body, 0)
                : 0;
            CharacterRecord record = null;
            if (session?.Account != null)
            {
                var characters = _characterRepository.ListByAccount(
                    session.Account.AccountId);
                if (characters.Count > 0)
                {
                    if (slot >= characters.Count)
                        slot = 0;
                    record = characters[slot];
                }
            }

            if (record == null)
            {
                record = _characterRepository.GetById(
                    _selectCharacterDataSource.GetSeedCharacterId());
            }
            selectedSlot = slot;
            return record;
        }

        private async Task<bool> LeaveCurrentCharacterForSelectionAsync(
            EnhancedClientSession session,
            int characterId)
        {
            using (await _characterTransitions.AcquireAsync(characterId))
            {
                if (!_sessionDirectory.TryGet(
                        characterId,
                        out var current)
                    || !ReferenceEquals(current, session))
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] SELECT_CHARACTER rejected for " +
                        $"stale session: cid={characterId} " +
                        $"session={session.SessionId}");
                    return false;
                }

                // Keep the directory entry until all fallible role cleanup is
                // complete. If a send fails, the normal disconnect path still
                // owns this generation and can finish teardown.
                await _expertJobStoreHandler.CloseSessionAsync(
                    session,
                    includeOwner: true);
                await _townHandler.NotifyLeaveAsync(session);
                _townHandler.PersistPosition(
                    session,
                    forceImmediate: true,
                    source: "select_character");
                DungeonRunLifecycle.EndRunOnTeardown(
                    session,
                    "select_character",
                    _dungeonInstances);
                if (!await _sessionDirectory.UnregisterAsync(
                        characterId,
                        session))
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] SELECT_CHARACTER generation " +
                        $"changed during cleanup: cid={characterId} " +
                        $"session={session.SessionId}");
                    return false;
                }

                InventoryContext.Unregister(
                    session.SessionId,
                    characterId);
                session.GameSession = null;
                session.PendingReturnSelectCharacterId = characterId;
                EnterCharacterSelectionState(session);
                return true;
            }
        }

        private async Task CleanupDisplacedSessionAsync(
            int characterId,
            EnhancedClientSession displaced)
        {
            if (displaced == null)
                return;

            try
            {
                await _expertJobStoreHandler.CloseSessionAsync(
                    displaced,
                    includeOwner: false);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] displaced expert cleanup failed " +
                    $"cid={characterId}: {ex}");
            }

            try
            {
                await _townHandler.NotifyLeaveAsync(displaced);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] displaced town cleanup failed " +
                    $"cid={characterId}: {ex}");
            }

            _townHandler.PersistPosition(
                displaced,
                forceImmediate: true,
                source: "select-displaced");
            try
            {
                var detachedForRejoin =
                    DungeonRunLifecycle.DetachRunOnNetworkDisconnect(
                        displaced,
                        _dungeonInstances);
                if (!detachedForRejoin)
                {
                    DungeonRunLifecycle.EndRunOnTeardown(
                        displaced,
                        "select-displaced",
                        _dungeonInstances);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] displaced dungeon cleanup failed " +
                    $"cid={characterId}: {ex}");
            }

            try
            {
                InventoryContext.Unregister(
                    displaced.SessionId,
                    characterId);
                displaced.GameSession = null;
                displaced.Player.TownPresenceReady = false;
                _dungeonRejoin.ClearSession(displaced.SessionId);
                _lotteryItemHandler.ClearSession(displaced.SessionId);
                _craneMiniGameHandler.ClearSession(displaced.SessionId);
                PetCreatureRuntimeService.UnregisterSession(displaced);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] displaced local cleanup failed " +
                    $"cid={characterId}: {ex}");
            }
            finally
            {
                try
                {
                    displaced.Close();
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] displaced close failed " +
                        $"cid={characterId}: {ex}");
                }
            }
        }

        private async Task RollbackSelectedSessionAsync(
            int characterId,
            EnhancedClientSession session)
        {
            var removed = false;
            try
            {
                removed = await _sessionDirectory.UnregisterAsync(
                    characterId,
                    session);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] selection rollback unregister failed " +
                    $"cid={characterId}: {ex}");
            }

            try
            {
                await _expertJobStoreHandler.CloseSessionAsync(
                    session,
                    includeOwner: false);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] selection rollback expert cleanup " +
                    $"failed cid={characterId}: {ex}");
            }

            if (removed
                && session.Player?.CharacterId == characterId)
            {
                try
                {
                    await _townHandler.NotifyLeaveAsync(session);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] selection rollback town cleanup " +
                        $"failed cid={characterId}: {ex}");
                }

                _townHandler.PersistPosition(
                    session,
                    forceImmediate: true,
                    source: "select-rollback");
                try
                {
                    DungeonRunLifecycle.EndRunOnTeardown(
                        session,
                        "select-rollback",
                        _dungeonInstances);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] selection rollback dungeon " +
                        $"cleanup failed cid={characterId}: {ex}");
                }
            }

            try
            {
                InventoryContext.Unregister(
                    session.SessionId,
                    characterId);
                session.GameSession = null;
                _dungeonRejoin.ClearSession(session.SessionId);
                _lotteryItemHandler.ClearSession(session.SessionId);
                _craneMiniGameHandler.ClearSession(session.SessionId);
                PetCreatureRuntimeService.UnregisterSession(session);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] selection rollback local cleanup " +
                    $"failed cid={characterId}: {ex}");
            }

            EnterCharacterSelectionState(session);
        }
    }
}
