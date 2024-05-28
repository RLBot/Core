using System.Net.Sockets;
using System.Threading.Channels;
using Google.FlatBuffers;
using MatchManagement;
using rlbot.flat;
using RLBotSecret.Conversion;
using RLBotSecret.Types;

namespace RLBotCS.Server
{
    internal enum SessionMessageType
    {
        DistributeGameState,
        StopMatch,
    }

    internal class SessionMessage
    {
        private SessionMessageType _type;
        private TypedPayload _gameState;

        public static SessionMessage DistributeGameState(TypedPayload gameState)
        {
            return new SessionMessage { _type = SessionMessageType.DistributeGameState, _gameState = gameState };
        }

        public static SessionMessage StopMatch()
        {
            return new SessionMessage { _type = SessionMessageType.StopMatch };
        }

        public SessionMessageType Type()
        {
            return _type;
        }

        public TypedPayload GetGameState()
        {
            return _gameState;
        }
    }

    internal class FlatbufferSession
    {
        private TcpClient _client;
        private int _clientId;
        private SocketSpecStreamReader _socketSpecReader;
        private SocketSpecStreamWriter _socketSpecWriter;

        private ChannelReader<SessionMessage> _incomingMessages;
        private ChannelWriter<ServerMessage> _rlbotServer;
        private ChannelWriter<BridgeMessage> _bridge;

        private bool _isReady = false;
        private bool _wantsBallPredictions = false;
        private bool _wantsGameMessages = false;
        private bool _wantsComms = false;
        private bool _closeAfterMatch = false;

        // Allocate 50kb to avoid failing to decode large messages
        // Match settings and render messages can be quite large
        // We don't want to limit bot devs - and it's only 50kb
        // That's 6.4mb of ram at 128 connections, which isn't much
        private FlatBufferBuilder _builder = new(51200);

        public FlatbufferSession(
            TcpClient client,
            int clientId,
            ChannelReader<SessionMessage> incomingMessages,
            ChannelWriter<ServerMessage> rlbotServer,
            ChannelWriter<BridgeMessage> bridge
        )
        {
            _client = client;
            _clientId = clientId;
            _incomingMessages = incomingMessages;
            _rlbotServer = rlbotServer;
            _bridge = bridge;

            NetworkStream stream = _client.GetStream();
            _socketSpecReader = new SocketSpecStreamReader(stream);
            _socketSpecWriter = new SocketSpecStreamWriter(stream);
        }

        private async Task<bool> ParseClientMessage(TypedPayload message)
        {
            var byteBuffer = new ByteBuffer(message.Payload.Array, message.Payload.Offset);

            switch (message.Type)
            {
                case DataType.None:
                    // The client requested that we close the connection
                    return false;

                case DataType.ReadyMessage:
                    var readyMsg = ReadyMessage.GetRootAsReadyMessage(byteBuffer);

                    _wantsBallPredictions = readyMsg.WantsBallPredictions;
                    _wantsGameMessages = readyMsg.WantsGameMessages;
                    _wantsComms = readyMsg.WantsComms;
                    _closeAfterMatch = readyMsg.CloseAfterMatch;

                    Channel<TypedPayload> matchSettingsChannel = Channel.CreateUnbounded<TypedPayload>();
                    Channel<FieldInfoT> fieldInfoChannel = Channel.CreateUnbounded<FieldInfoT>();

                    _rlbotServer.TryWrite(
                        ServerMessage.IntroDataRequest(matchSettingsChannel.Writer, fieldInfoChannel.Writer)
                    );

                    TypedPayload matchSettings = await matchSettingsChannel.Reader.ReadAsync();
                    await SendPayloadToClientAsync(matchSettings);
                    Console.WriteLine("Sent match settings to client.");

                    FieldInfoT fieldInfo = await fieldInfoChannel.Reader.ReadAsync();

                    _builder.Finish(FieldInfo.Pack(_builder, fieldInfo).Value);
                    TypedPayload fieldInfoMessage = TypedPayload.FromFlatBufferBuilder(
                        DataType.FieldInfo,
                        _builder
                    );
                    await SendPayloadToClientAsync(fieldInfoMessage);
                    Console.WriteLine("Sent field info to client.");

                    _isReady = true;
                    break;

                case DataType.StopCommand:
                    Console.WriteLine("Core got stop command from client.");
                    var stopCommand = StopCommand.GetRootAsStopCommand(byteBuffer).UnPack();
                    _rlbotServer.TryWrite(ServerMessage.StopMatch(stopCommand.ShutdownServer));
                    break;

                case DataType.StartCommand:
                    Console.WriteLine("Core got start command from client.");
                    var startCommand = StartCommand.GetRootAsStartCommand(byteBuffer).UnPack();
                    var tomlMatchSettings = ConfigParser.GetMatchSettings(startCommand.ConfigPath);

                    _builder.Clear();
                    _builder.Finish(MatchSettings.Pack(_builder, tomlMatchSettings).Value);
                    TypedPayload matchSettingsMessage = TypedPayload.FromFlatBufferBuilder(
                        DataType.MatchSettings,
                        _builder
                    );

                    _rlbotServer.TryWrite(ServerMessage.StartMatch(matchSettingsMessage, tomlMatchSettings));
                    break;

                case DataType.MatchSettings:
                    var matchSettingsT = MatchSettings.GetRootAsMatchSettings(byteBuffer).UnPack();
                    _rlbotServer.TryWrite(ServerMessage.StartMatch(message, matchSettingsT));
                    break;

                case DataType.PlayerInput:
                    var playerInputMsg = PlayerInput.GetRootAsPlayerInput(byteBuffer);
                    _bridge.TryWrite(BridgeMessage.PlayerInput(playerInputMsg));
                    break;

                case DataType.MatchComms:
                    break;

                // case DataType.RenderGroup:
                //     if (!_renderingIsEnabled)
                //     {
                //         break;
                //     }

                //     var renderingGroup = RenderGroup.GetRootAsRenderGroup(byteBuffer).UnPack();

                //     // If a group already exists with the same id,
                //     // remove the old render items
                //     RemoveRenderGroup(renderingGroup.Id);

                //     List<ushort> renderIds = new();

                //     // Create render requests
                //     foreach (var renderMessage in renderingGroup.RenderMessages)
                //     {
                //         if (RenderItem(renderMessage.Variety) is ushort renderId)
                //         {
                //             renderIds.Add(renderId);
                //         }
                //     }

                //     // Add the new render items to the tracker
                //     _sessionRenderIds[renderingGroup.Id] = renderIds;

                //     // Send the render requests
                //     _gameController.RenderingSender.Send();

                //     break;

                // case DataType.RemoveRenderGroup:
                //     var removeRenderGroup = rlbot
                //         .flat.RemoveRenderGroup.GetRootAsRemoveRenderGroup(byteBuffer)
                //         .UnPack();
                //     RemoveRenderGroup(removeRenderGroup.Id);
                //     break;

                // case DataType.DesiredGameState:
                //     if (!_stateSettingIsEnabled)
                //     {
                //         break;
                //     }

                //     var desiredGameState = DesiredGameState.GetRootAsDesiredGameState(byteBuffer).UnPack();
                //     _gameController.MatchStarter.SetDesiredGameState(desiredGameState);
                //     break;
                default:
                    // Console.WriteLine("Core got unexpected message type {0} from client.", message.Type);
                    break;
            }

            return true;
        }

        private async Task SendPayloadToClientAsync(TypedPayload payload)
        {
            await _socketSpecWriter.WriteAsync(payload);
            await _socketSpecWriter.SendAsync();
        }

        private async Task HandleIncomingMessages()
        {
            await foreach (SessionMessage message in _incomingMessages.ReadAllAsync())
            {
                switch (message.Type())
                {
                    case SessionMessageType.DistributeGameState:
                        if (_isReady)
                        {
                            await SendPayloadToClientAsync(message.GetGameState());
                        }
                        break;
                    case SessionMessageType.StopMatch:
                        if (_isReady && _closeAfterMatch)
                        {
                            Console.WriteLine("Core got stop match message from server.");
                            return;
                        }

                        break;
                }
            }
        }

        private async Task HandleClientMessages()
        {
            await foreach (TypedPayload message in _socketSpecReader.ReadAllAsync())
            {
                bool keepRunning = await ParseClientMessage(message);
                if (!keepRunning)
                {
                    Console.WriteLine("Core got close message from client.");
                    return;
                }
            }
        }

        public void BlockingRun()
        {
            Task incomingMessagesTask = HandleIncomingMessages();
            Task clientMessagesTask = HandleClientMessages();

            Task.WhenAny(incomingMessagesTask, clientMessagesTask).Wait();
        }

        public void Cleanup()
        {
            // try to politely close the connection
            try
            {
                TypedPayload msg = new() { Type = DataType.None, Payload = new ArraySegment<byte>([1]), };
                SendPayloadToClientAsync(msg).Wait();
            }
            catch (Exception)
            {
                // client disconnected first
            }

            _rlbotServer.TryWrite(ServerMessage.SessionClosed(_clientId));
            _client.Close();
        }
    }
}
