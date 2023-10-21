﻿using Gazillion;
using MHServerEmu.Common.Logging;
using MHServerEmu.Games;
using MHServerEmu.Games.Entities.Options;
using MHServerEmu.Networking;

namespace MHServerEmu.PlayerManagement
{
    public class PlayerManagerService : IGameService
    {
        private const ushort MuxChannel = 1;   // All messages come to and from PlayerManager over mux channel 1

        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly ServerManager _serverManager;
        private readonly GameManager _gameManager;
        private readonly object _playerLock = new();
        private readonly List<FrontendClient> _playerList = new();

        public PlayerManagerService(ServerManager serverManager)
        {
            _serverManager = serverManager;
            _gameManager = new(_serverManager);
        }

        public void AddPlayer(FrontendClient client)
        {
            lock (_playerLock)
            {
                if (_playerList.Contains(client))
                {
                    Logger.Warn("Failed to add player: already added");
                    return;
                }

                _playerList.Add(client);
                _gameManager.GetAvailableGame().AddPlayer(client);
            }
        }

        public void RemovePlayer(FrontendClient client)
        {
            lock (_playerLock)
            {
                if (_playerList.Contains(client) == false)
                {
                    Logger.Warn("Failed to remove player: not found");
                    return;
                }

                _playerList.Remove(client);
            }
        }

        public void BroadcastMessage(GameMessage message)
        {
            lock (_playerLock)
            {
                foreach (FrontendClient player in _playerList)
                    player.SendMessage(MuxChannel, message);
            }
        }

        public Game GetGameByPlayer(FrontendClient client) => _gameManager.GetGameById(client.GameId);

        #region Message Handling

        public void Handle(FrontendClient client, ushort muxId, GameMessage message)
        {
            switch ((ClientToGameServerMessage)message.Id)
            {
                case ClientToGameServerMessage.NetMessageReadyForGameJoin:
                    // NetMessageReadyForGameJoin contains a bug where wipesDataIfMismatchedInDb is marked as required but the client doesn't include it
                    // To avoid an exception we build a partial message from the data we receive
                    Logger.Info($"Received NetMessageReadyForGameJoin from {client.Session.Account}");
                    var parsedReadyForGameJoin = NetMessageReadyForGameJoin.CreateBuilder().MergeFrom(message.Payload).BuildPartial();
                    Logger.Trace(parsedReadyForGameJoin.ToString());

                    // Log the player in
                    Logger.Info($"Logging in player {client.Session.Account}");
                    client.SendMessage(muxId, new(NetMessageReadyAndLoggedIn.DefaultInstance)); // add report defect (bug) config here

                    // Sync time
                    client.SendMessage(muxId, new(NetMessageInitialTimeSync.CreateBuilder()
                        .SetGameTimeServerSent(161351679299542)     // dumped - Gazillion time?
                        .SetDateTimeServerSent(1509657957345525)    // dumped - unix time stamp in microseconds
                        .Build()));

                    break;

                case ClientToGameServerMessage.NetMessageSyncTimeRequest:
                    /* NOTE: this is old experimental code
                    Logger.Info($"Received NetMessageSyncTimeRequest");
                    var parsedSyncTimeRequestMessage = NetMessageSyncTimeRequest.ParseFrom(message.Content);
                    Logger.Trace(parsedSyncTimeRequestMessage.ToString());

                    Logger.Info("Sending NetMessageSyncTimeReply");
                    client.SendMessage(muxId, new(NetMessageSyncTimeReply.CreateBuilder()
                        .SetGameTimeClientSent(parsedSyncTimeRequestMessage.GameTimeClientSent)
                        .SetGameTimeServerReceived(_gameServerManager.GetGameTime())
                        .SetGameTimeServerSent(_gameServerManager.GetGameTime())

                        .SetDateTimeClientSent(parsedSyncTimeRequestMessage.DateTimeClientSent)
                        .SetDateTimeServerReceived(_gameServerManager.GetDateTime())
                        .SetDateTimeServerSent(_gameServerManager.GetDateTime())

                        .SetDialation(1.0f)
                        .SetGametimeDialationStarted(0)
                        .SetDatetimeDialationStarted(0)
                        .Build()));
                    */

                    break;

                case ClientToGameServerMessage.NetMessagePing:
                    /*
                    Logger.Info($"Received NetMessagePing");
                    var parsedPingMessage = NetMessagePing.ParseFrom(message.Content);
                    Logger.Trace(parsedPingMessage.ToString());
                    */
                    break;

                case ClientToGameServerMessage.NetMessageFPS:
                    /*
                    Logger.Info("Received FPS");
                    var fps = NetMessageFPS.ParseFrom(message.Content);
                    Logger.Trace(fps.ToString());
                    */
                    break;

                // Game messages
                case ClientToGameServerMessage.NetMessageUpdateAvatarState:
                case ClientToGameServerMessage.NetMessageCellLoaded:
                case ClientToGameServerMessage.NetMessageTryActivatePower:
                case ClientToGameServerMessage.NetMessagePowerRelease:
                case ClientToGameServerMessage.NetMessageTryCancelPower:
                case ClientToGameServerMessage.NetMessageTryCancelActivePower:
                case ClientToGameServerMessage.NetMessageContinuousPowerUpdateToServer:
                case ClientToGameServerMessage.NetMessageTryInventoryMove:
                case ClientToGameServerMessage.NetMessageThrowInteraction:
                case ClientToGameServerMessage.NetMessageUseInteractableObject:
                case ClientToGameServerMessage.NetMessageUseWaypoint:
                case ClientToGameServerMessage.NetMessageSwitchAvatar:
                case ClientToGameServerMessage.NetMessageRequestInterestInAvatarEquipment:
                case ClientToGameServerMessage.NetMessageSelectOmegaBonus:                      // This should be within NetMessageOmegaBonusAllocationCommit only in theory
                case ClientToGameServerMessage.NetMessageOmegaBonusAllocationCommit:
                case ClientToGameServerMessage.NetMessageRespecOmegaBonus:
                    GetGameByPlayer(client).Handle(client, muxId, message);
                    break;

                // Grouping Manager messages
                case ClientToGameServerMessage.NetMessageChat:
                case ClientToGameServerMessage.NetMessageTell:
                case ClientToGameServerMessage.NetMessageReportPlayer:
                case ClientToGameServerMessage.NetMessageChatBanVote:
                    _serverManager.GroupingManagerService.Handle(client, muxId, message);
                    break;

                // Billing messages
                case ClientToGameServerMessage.NetMessageGetCatalog:
                case ClientToGameServerMessage.NetMessageGetCurrencyBalance:
                case ClientToGameServerMessage.NetMessageBuyItemFromCatalog:
                case ClientToGameServerMessage.NetMessageBuyGiftForOtherPlayer:
                case ClientToGameServerMessage.NetMessagePurchaseUnlock:
                case ClientToGameServerMessage.NetMessageGetGiftHistory:
                    _serverManager.BillingService.Handle(client, muxId, message);
                    break;

                case ClientToGameServerMessage.NetMessageGracefulDisconnect:
                    client.SendMessage(muxId, new(NetMessageGracefulDisconnectAck.DefaultInstance));
                    break;

                case ClientToGameServerMessage.NetMessageSetPlayerGameplayOptions:
                    Logger.Trace(new GameplayOptions(NetMessageSetPlayerGameplayOptions.ParseFrom(message.Payload).OptionsData).ToString());
                    break;

                default:
                    Logger.Warn($"Received unhandled message {(ClientToGameServerMessage)message.Id} (id {message.Id})");
                    break;
            }
        }

        public void Handle(FrontendClient client, ushort muxId, IEnumerable<GameMessage> messages)
        {
            foreach (GameMessage message in messages) Handle(client, muxId, message);
        }

        #endregion
    }
}