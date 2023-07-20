﻿using System.Net.Sockets;
using Google.ProtocolBuffers;
using Gazillion;
using MHServerEmu.Common;
using MHServerEmu.GameServer;

namespace MHServerEmu.Networking
{
    public class FrontendClient
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly Socket socket;
        private readonly NetworkStream stream;

        private readonly GameServerManager _gameServerManager;

        public bool FinishedPlayerMgrServerFrontendHandshake { get; set; } = false;
        public bool FinishedGroupingManagerFrontendHandshake { get; set; } = false;

        // Flags for hardcoded initialization
        public bool InitReceivedFirstNetMessagePlayerTradeCancel { get; set; } = false;
        public bool InitReceivedSecondNetMessagePlayerTradeCancel { get; set; } = false;
        public bool InitReceivedFirstNetMessageVanityTitleSelect { get; set; } = false;
        public bool InitReceivedFirstNetMessageRequestInterestInInventory { get; set; } = false;
        public bool InitReceivedFirstNetMessageCellLoaded { get; set; } = false;

        public FrontendClient(Socket socket, GameServerManager gameServerManager)
        {
            this.socket = socket;
            stream = new NetworkStream(socket);

            _gameServerManager = gameServerManager;
        }

        public void Run()
        {
            try
            {
                CodedInputStream stream = CodedInputStream.CreateInstance(this.stream);

                while (!stream.IsAtEnd)
                {
                    Handle(stream);
                }
                Logger.Info("Client disconnected");
            }
            catch (Exception e)
            {
                Logger.Error(e.ToString());
            }
        }

        public void Disconnect()
        {
            socket.Disconnect(false);
        }

        public void SendGameMessage(ushort muxId, byte messageId, byte[] message, bool addExtraByte = false)
        {
            ServerPacket packet = new(muxId, MuxCommand.Message);
            packet.WriteMessage(messageId, message, addExtraByte);
            Send(packet);
        }

        public void SendPacketFromFile(string fileName)
        {
            string path = $"{Directory.GetCurrentDirectory()}\\Assets\\Packets\\{fileName}";

            if (File.Exists(path))
            {
                Logger.Info($"Sending {fileName}");
                SendRaw(File.ReadAllBytes(path));
            }
            else
            {
                Logger.Warn($"{fileName} not found");
            }
        }

        private void Handle(CodedInputStream stream)
        {
            ClientPacket packet = new(stream);
            Logger.Trace($"IN: {packet.RawData.ToHexString()}");

            switch (packet.Command)
            {
                case MuxCommand.Connect:
                    Logger.Info($"Received connect for MuxId {packet.MuxId}");
                    Logger.Info($"Sending accept for MuxId {packet.MuxId}");
                    Send(new(packet.MuxId, MuxCommand.Accept));
                    break;

                case MuxCommand.Accept:
                    Logger.Info($"Received accept for MuxId {packet.MuxId}");
                    break;

                case MuxCommand.Disconnect:
                    Logger.Info($"Received disconnect for MuxId {packet.MuxId}");
                    break;

                case MuxCommand.Insert:
                    Logger.Info($"Received insert for MuxId {packet.MuxId}");
                    break;

                case MuxCommand.Message:
                    Logger.Trace($"Received message on MuxId {packet.MuxId} ({packet.BodyLength} bytes)");

                    // First byte is message id, second byte is generally protobuf size as uint8
                    // Some messages have weird structure, need to figure this out
                    byte[] message = new byte[packet.Body[1]];
                    for (int i = 0; i < message.Length; i++) message[i] = packet.Body[i + 2];

                    _gameServerManager.Handle(this, packet.MuxId, packet.Body[0], message);        

                    break;
            }
        }

        private void Send(ServerPacket packet)
        {
            byte[] data = packet.Data;
            Logger.Trace($"OUT: {data.ToHexString()}");
            stream.Write(data, 0, data.Length);
        }

        private void SendRaw(byte[] data)
        {
            Logger.Trace($"OUT: raw {data.Length} bytes");
            stream.Write(data, 0, data.Length);
        }
    }
}