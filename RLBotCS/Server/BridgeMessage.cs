﻿using Bridge.Models.Command;
using Bridge.Models.Control;
using Bridge.Models.Message;
using Bridge.State;
using rlbot.flat;
using RLBotCS.Conversion;
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
        CarInput carInput = FlatToModel.ToCarInput(PlayerInput.ControllerState);
        ushort? actorId = context.GameState.PlayerMapping.ActorIdFromPlayerIndex(
            PlayerInput.PlayerIndex
        );

        if (actorId.HasValue)
        {
            Bridge.Models.Control.PlayerInput playerInput =
                new() { ActorId = actorId.Value, CarInput = carInput };
            context.PlayerInputSender.SendPlayerInput(playerInput);
        }
        else
            Console.WriteLine(
                "Core got input from unknown player index {0}",
                PlayerInput.PlayerIndex
            );
    }
}

internal record SpawnHuman(PlayerConfigurationT PlayerConfig, uint DesiredIndex)
    : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        context.QueueConsoleCommand("ChangeTeam " + PlayerConfig.Team);
        context.GameState.PlayerMapping.AddPendingSpawn(
            new SpawnTracker
            {
                CommandId = 0,
                SpawnId = PlayerConfig.SpawnId,
                DesiredPlayerIndex = DesiredIndex,
                IsBot = false,
                IsCustomBot = false
            }
        );
    }
}

internal record SpawnBot(
    PlayerConfigurationT PlayerConfig,
    BotSkill Skill,
    uint DesiredIndex,
    bool IsCustomBot
) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context)
    {
        PlayerConfigurationT config = PlayerConfig;
        PlayerMetadata? alreadySpawnedPlayer = context
            .GameState.PlayerMapping.GetKnownPlayers()
            .FirstOrDefault(kp => config.SpawnId == kp.SpawnId);
        if (alreadySpawnedPlayer != null)
            // We've already spawned this player, don't duplicate them.
            return;

        config.Loadout ??= new PlayerLoadoutT();
        config.Loadout.LoadoutPaint ??= new LoadoutPaintT();
        Loadout loadout = FlatToModel.ToLoadout(config.Loadout, config.Team);

        context.QueuedMatchCommands = true;
        ushort commandId = context.MatchCommandSender.AddBotSpawnCommand(
            config.Name,
            (int)config.Team,
            Skill,
            loadout
        );

        context.GameState.PlayerMapping.AddPendingSpawn(
            new SpawnTracker
            {
                CommandId = commandId,
                SpawnId = config.SpawnId,
                DesiredPlayerIndex = DesiredIndex,
                IsCustomBot = IsCustomBot,
                IsBot = true
            }
        );
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
        Console.WriteLine("Core is about to start match with command: " + loadMapCommand);

        context.MatchCommandSender.AddConsoleCommand(loadMapCommand);
        context.MatchCommandSender.Send();
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
                var currentPhysics = context.GameState.GameCars[(ushort)id].Physics;
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

internal record ShowQuickChat(MatchCommT MatchComm) : IBridgeMessage
{
    public void HandleMessage(BridgeContext context) =>
        context.QuickChat.AddChat(MatchComm, context.GameState.SecondsElapsed);
}
