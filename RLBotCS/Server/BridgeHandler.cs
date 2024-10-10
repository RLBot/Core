using System.Threading.Channels;
using Bridge.Conversion;
using Bridge.TCP;
using Microsoft.Extensions.Logging;
using rlbot.flat;
using RLBotCS.Conversion;
using RLBotCS.Server.FlatbuffersMessage;
using GameStatus = Bridge.Models.Message.GameStatus;

namespace RLBotCS.Server;

internal class BridgeHandler(
    ChannelWriter<IServerMessage> writer,
    ChannelReader<IBridgeMessage> reader,
    TcpMessenger messenger
)
{
    private const int MAX_TICK_SKIP = 1;
    private readonly BridgeContext _context = new(writer, reader, messenger);

    private async Task HandleIncomingMessages()
    {
        await foreach (IBridgeMessage message in _context.Reader.ReadAllAsync())
            lock (_context)
            {
                try
                {
                    message.HandleMessage(_context);
                }
                catch (Exception e)
                {
                    _context.Logger.LogError(
                        $"Error while handling message in BridgeHandler: {e}"
                    );
                }
            }
    }

    private async Task HandleServer()
    {
        await _context.Messenger.WaitForConnectionAsync();

        await foreach (var messageClump in _context.Messenger.ReadAllAsync())
        {
            lock (_context)
            {
                if (!_context.GotFirstMessage)
                {
                    _context.GotFirstMessage = true;
                    _context.Writer.TryWrite(new StartCommunication());
                }

                // reset the counter that lets us know if we're sending too many bytes
                messenger.ResetByteCount();

                float prevTime = _context.GameState.SecondsElapsed;
                _context.GameState = MessageHandler.CreateUpdatedState(
                    messageClump,
                    _context.GameState
                );

                float deltaTime = _context.GameState.SecondsElapsed - prevTime;
                bool timeAdvanced = deltaTime > 0.001;
                if (timeAdvanced)
                    _context.ticksSkipped = 0;
                else
                    _context.ticksSkipped++;

                if (timeAdvanced)
                    _context.PerfMonitor.AddRLBotSample(deltaTime);

                GamePacketT? packet =
                    timeAdvanced
                    || (
                        _context.ticksSkipped > MAX_TICK_SKIP
                        && (
                            _context.GameState.GameStatus == GameStatus.Replay
                            || _context.GameState.GameStatus == GameStatus.Paused
                            || _context.GameState.GameStatus == GameStatus.Ended
                            || _context.GameState.GameStatus == GameStatus.Inactive
                        )
                    )
                        ? _context.GameState.ToFlatBuffers()
                        : null;
                _context.Writer.TryWrite(new DistributeGameState(_context.GameState, packet));

                var matchStarted = MessageHandler.ReceivedMatchInfo(messageClump);
                if (matchStarted)
                {
                    // _context.Logger.LogInformation("Map name: " + _context.GameState.MapName);
                    _context.RenderingMgmt.ClearAllRenders(_context.MatchCommandSender);
                    _context.MatchHasStarted = true;
                    _context.Writer.TryWrite(new MapSpawned(_context.GameState.MapName));
                }

                if (_context.GameState.MatchEnded)
                {
                    // reset everything
                    _context.QuickChat.ClearChats();
                    _context.PerfMonitor.ClearAll();
                }
                else if (
                    _context.GameState.GameStatus != GameStatus.Replay
                    && _context.GameState.GameStatus != GameStatus.Paused
                    && timeAdvanced
                )
                {
                    // only rerender if we're not in a replay or paused
                    _context.QuickChat.RenderChats(_context.RenderingMgmt, _context.GameState);

                    // only render if we're not in a goal scored or ended state
                    if (_context.GameState.GameStatus != GameStatus.GoalScored)
                        _context.PerfMonitor.RenderSummary(
                            _context.RenderingMgmt,
                            _context.GameState,
                            deltaTime
                        );
                    else
                        _context.PerfMonitor.ClearAll();
                }

                if (
                    _context is
                    {
                        DelayMatchCommandSend: true,
                        QueuedMatchCommands: true,
                        QueuingCommandsComplete: true,
                        MatchHasStarted: true,
                        GameState.GameStatus: GameStatus.Paused
                    }
                )
                {
                    // If we send the commands before the map has spawned, nothing will happen
                    _context.DelayMatchCommandSend = false;
                    _context.QueuedMatchCommands = false;

                    _context.Logger.LogInformation("Sending delayed match commands");
                    _context.MatchCommandSender.Send();
                }

                _context.RenderingMgmt.SendRenderClears();
            }
        }
    }

    public void BlockingRun() =>
        Task.WhenAny(Task.Run(HandleIncomingMessages), Task.Run(HandleServer)).Wait();

    public void Cleanup()
    {
        lock (_context)
        {
            _context.Writer.TryComplete();

            try
            {
                _context.RenderingMgmt.ClearAllRenders(_context.MatchCommandSender);

                // we can only clear so many renders each tick
                // so we do this until we've cleared them all
                // or rocket league has been closed
                while (!_context.RenderingMgmt.SendRenderClears())
                {
                    if (!messenger.WaitForAnyMessageAsync().Result)
                        break;

                    messenger.ResetByteCount();
                }
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            catch (Exception e)
            {
                _context.Logger.LogError($"Error while cleaning up BridgeHandler: {e}");
            }
            finally
            {
                _context.Messenger.Dispose();
            }
        }
    }
}
