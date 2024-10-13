﻿using System.Threading.Channels;
using Bridge.Models.Command;
using Bridge.Models.Message;
using Bridge.State;
using Microsoft.Extensions.Logging;
using rlbot.flat;
using RLBotCS.Conversion;
using RLBotCS.ManagerTools;
using PlayerInput = rlbot.flat.PlayerInput;

namespace RLBotCS.Server;

internal interface IBridgeMessage
{
    public void HandleMessage(BridgeContext context);
}

internal record Input(PlayerInputT PlayerInput) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        ushort? actorId = context.GameState.PlayerMapping.ActorIdFromPlayerIndex(
            PlayerInput.PlayerIndex
        );

        if (actorId is ushort actorIdValue)
        {
            Bridge.Models.Control.PlayerInput playerInput =
                new()
                {
                    ActorId = actorIdValue,
                    CarInput = FlatToModel.ToCarInput(PlayerInput.ControllerState)
                };
            context.PlayerInputSender.SendPlayerInput(playerInput);
        }
        else if (
            !context.GameState.PlayerMapping.IsPlayerIndexPending(PlayerInput.PlayerIndex)
        )
            context.Logger.LogError(
                $"Got input from unknown player index {PlayerInput.PlayerIndex}"
            );
    }
}

internal record SpawnHuman(PlayerConfigurationT Config, uint DesiredIndex) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        context.QueueConsoleCommand("ChangeTeam " + Config.Team);

        PlayerMetadata? alreadySpawnedPlayer = context
            .GameState.PlayerMapping.GetKnownPlayers()
            .FirstOrDefault(kp => Config.SpawnId == kp.SpawnId);
        if (alreadySpawnedPlayer is PlayerMetadata metadata)
        {
            metadata.PlayerIndex = DesiredIndex;
            return;
        }

        context.GameState.PlayerMapping.AddPendingSpawn(
            new SpawnTracker
            {
                CommandId = 0,
                SpawnId = Config.SpawnId,
                DesiredPlayerIndex = DesiredIndex,
                IsBot = false,
                IsCustomBot = false
            }
        );
    }
}

internal record SpawnBot(
    PlayerConfigurationT Config,
    BotSkill Skill,
    uint DesiredIndex,
    bool IsCustomBot
) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        PlayerMetadata? alreadySpawnedPlayer = context
            .GameState.PlayerMapping.GetKnownPlayers()
            .FirstOrDefault(kp => Config.SpawnId == kp.SpawnId);
        if (alreadySpawnedPlayer != null)
            // We've already spawned this player, don't duplicate them.
            return;

        Config.Loadout ??= new PlayerLoadoutT();
        Config.Loadout.LoadoutPaint ??= new LoadoutPaintT();
        Loadout loadout = FlatToModel.ToLoadout(Config.Loadout, Config.Team);

        context.QueuedMatchCommands = true;
        context.QueuingCommandsComplete = false;
        ushort commandId = context.MatchCommandSender.AddBotSpawnCommand(
            Config.Name,
            (int)Config.Team,
            Skill,
            loadout
        );

        context.GameState.PlayerMapping.AddPendingSpawn(
            new SpawnTracker
            {
                CommandId = commandId,
                SpawnId = Config.SpawnId,
                DesiredPlayerIndex = DesiredIndex,
                IsCustomBot = IsCustomBot,
                IsBot = true
            }
        );
    }
}

internal record MarkQueuingComplete() : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        context.QueuingCommandsComplete = true;
    }
}

internal record RemoveOldPlayers(List<int> spawnIds) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        foreach (int spawnId in spawnIds)
        {
            PlayerMetadata? player = context
                .GameState.PlayerMapping.GetKnownPlayers()
                .FirstOrDefault(p => p.SpawnId == spawnId);

            if (player != null)
            {
                context.MatchCommandSender.AddDespawnCommand(player.ActorId);
            }
        }
    }
}

internal record ConsoleCommand(string Command) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context) => context.QueueConsoleCommand(Command);
}

internal record SpawnMap(MatchSettingsT MatchSettings) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        context.MatchHasStarted = false;
        context.DelayMatchCommandSend = true;

        string loadMapCommand = FlatToCommand.MakeOpenCommand(MatchSettings);
        context.Logger.LogInformation($"Starting match with command: {loadMapCommand}");

        context.MatchCommandSender.AddConsoleCommand(loadMapCommand);
        context.MatchCommandSender.Send();
    }
}

internal record FlushMatchCommands() : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        if (!context.QueuedMatchCommands)
            return;

        context.MatchCommandSender.Send();
        context.DelayMatchCommandSend = false;
        context.QueuedMatchCommands = false;
    }
}

internal record AddRenders(int ClientId, int RenderId, List<RenderMessageT> RenderItems)
    : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        lock (context)
            context.RenderingMgmt.AddRenderGroup(
                ClientId,
                RenderId,
                RenderItems,
                context.GameState
            );
    }
}

internal record RemoveRenders(int ClientId, int RenderId) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context) =>
        context.RenderingMgmt.RemoveRenderGroup(ClientId, RenderId);
}

internal record RemoveClientRenders(int ClientId) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context) =>
        context.RenderingMgmt.ClearClientRenders(ClientId);
}

internal record SetMutators(MutatorSettingsT MutatorSettings) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        context.GameState.GameTimeRemaining = MutatorSettings.MatchLength switch
        {
            MatchLength.Five_Minutes => 5 * 60,
            MatchLength.Ten_Minutes => 10 * 60,
            MatchLength.Twenty_Minutes => 20 * 60,
            MatchLength.Unlimited => 0,
            _
                => throw new ArgumentOutOfRangeException(
                    nameof(MutatorSettings.MatchLength),
                    MutatorSettings.MatchLength,
                    null
                )
        };

        context.GameState.MatchLength = MutatorSettings.MatchLength switch
        {
            MatchLength.Five_Minutes => Bridge.Packet.MatchLength.FiveMinutes,
            MatchLength.Ten_Minutes => Bridge.Packet.MatchLength.TenMinutes,
            MatchLength.Twenty_Minutes => Bridge.Packet.MatchLength.TwentyMinutes,
            MatchLength.Unlimited => Bridge.Packet.MatchLength.Unlimited,
            _
                => throw new ArgumentOutOfRangeException(
                    nameof(MutatorSettings.MatchLength),
                    MutatorSettings.MatchLength,
                    null
                )
        };

        context.GameState.RespawnTime = MutatorSettings.RespawnTimeOption switch
        {
            RespawnTimeOption.Three_Seconds => 3,
            RespawnTimeOption.Two_Seconds => 2,
            RespawnTimeOption.One_Second => 1,
            RespawnTimeOption.Disable_Goal_Reset => 3,
            _
                => throw new ArgumentOutOfRangeException(
                    nameof(MutatorSettings.RespawnTimeOption),
                    MutatorSettings.RespawnTimeOption,
                    null
                )
        };
    }
}

internal record SetGameState(DesiredGameStateT GameState) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        foreach (var command in GameState.ConsoleCommands)
            context.MatchCommandSender.AddConsoleCommand(command.Command);

        if (GameState.GameInfoState is DesiredGameInfoStateT gameInfo)
        {
            if (gameInfo.WorldGravityZ is FloatT gravity)
                context.MatchCommandSender.AddConsoleCommand(
                    FlatToCommand.MakeGravityCommand(gravity.Val)
                );

            if (gameInfo.GameSpeed is FloatT speed)
                context.MatchCommandSender.AddConsoleCommand(
                    FlatToCommand.MakeGameSpeedCommand(speed.Val)
                );

            if (gameInfo.Paused is BoolT paused)
                context.MatchCommandSender.AddSetPausedCommand(paused.Val);

            if (gameInfo.EndMatch is BoolT endMatch && endMatch.Val)
                context.MatchCommandSender.AddMatchEndCommand();
        }

        for (int i = 0; i < GameState.BallStates.Count; i++)
        {
            var ball = GameState.BallStates[i];
            var id = context.GameState.GetBallActorIdFromIndex((uint)i);

            if (id == null)
                continue;

            if (ball.Physics is DesiredPhysicsT physics)
            {
                var currentPhysics = context.GameState.Balls[(ushort)id].Physics;
                var fullState = FlatToModel.DesiredToPhysics(physics, currentPhysics);

                context.MatchCommandSender.AddSetPhysicsCommand((ushort)id, fullState);
            }
        }

        for (int i = 0; i < GameState.CarStates.Count; i++)
        {
            var car = GameState.CarStates[i];
            var id = context.GameState.PlayerMapping.ActorIdFromPlayerIndex((uint)i);

            if (id == null)
                continue;

            if (car.Physics is DesiredPhysicsT physics)
            {
                var currentPhysics = context.GameState.GameCars[(uint)i].Physics;
                var fullState = FlatToModel.DesiredToPhysics(physics, currentPhysics);

                context.MatchCommandSender.AddSetPhysicsCommand((ushort)id, fullState);
            }

            if (car.BoostAmount is FloatT boostAmount)
            {
                context.MatchCommandSender.AddSetBoostCommand(
                    (ushort)id,
                    (int)boostAmount.Val
                );
            }
        }

        context.MatchCommandSender.Send();
    }
}

internal record EndMatch() : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        context.MatchCommandSender.AddMatchEndCommand();
        context.MatchCommandSender.Send();
    }
}

internal record ClearRenders() : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        context.QuickChat.ClearChats();
        context.PerfMonitor.ClearAll();
        context.RenderingMgmt.ClearAllRenders(context.MatchCommandSender);
    }
}

internal record ShowQuickChat(MatchCommT MatchComm) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context) =>
        context.QuickChat.AddChat(MatchComm, context.GameState.SecondsElapsed);
}

internal record AddPerfSample(uint Index, bool GotInput) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        if (context.GameState.GameCars.TryGetValue(Index, out var car))
            context.PerfMonitor.AddSample(car.Name, GotInput);
    }
}

internal record ClearProcessPlayerReservation(MatchSettingsT MatchSettings) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context) =>
        context.AgentReservation.SetPlayers(MatchSettings);
}

internal record PlayerInfoRequest(
    ChannelWriter<SessionMessage> SessionWriter,
    MatchSettingsT MatchSettings,
    string AgentId
) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        bool isHivemind = false;
        bool isScript = true;

        foreach (var player in MatchSettings.PlayerConfigurations)
        {
            if (player.AgentId == AgentId)
            {
                isScript = false;
                isHivemind = player.Hivemind;
                break;
            }
        }

        if (isHivemind)
        {
            if (
                context.AgentReservation.ReservePlayers(AgentId) is

                (List<PlayerIdPair>, uint) players
            )
            {
                SessionWriter.TryWrite(
                    new SessionMessage.PlayerIdPairs(players.Item2, players.Item1)
                );
            }
            else
            {
                context.Logger.LogError(
                    $"Failed to reserve players for hivemind with agent id {AgentId}"
                );
            }
        }
        else if (isScript)
        {
            for (var i = 0; i < MatchSettings.ScriptConfigurations.Count; i++)
            {
                var script = MatchSettings.ScriptConfigurations[i];
                if (script.AgentId == AgentId)
                {
                    PlayerIdPair player = new() { Index = (uint)i, SpawnId = script.SpawnId };
                    SessionWriter.TryWrite(
                        new SessionMessage.PlayerIdPairs(2, new() { player })
                    );
                    return;
                }
            }

            context.Logger.LogError($"Failed to find script with agent id {AgentId}");
        }
        else if (
            context.AgentReservation.ReservePlayer(AgentId) is (PlayerIdPair, uint) player
        )
        {
            SessionWriter.TryWrite(
                new SessionMessage.PlayerIdPairs(player.Item2, new() { player.Item1 })
            );
        }
        else
        {
            context.Logger.LogError($"Failed to reserve player for agent id {AgentId}");
        }
    }
}

internal record UnreservePlayers(uint team, List<PlayerIdPair> players) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context) =>
        context.AgentReservation.UnreservePlayers(team, players);
}
