﻿using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RLBotCS.ManagerTools
{
    internal class Launching
    {
        public static string SteamGameId = "252950";
        public static int RLBotSocketsPort = 23234;

        private static int DefaultGamePort = 50000;
        private static int IdealGamePort = 23233;

        public static int FindUsableGamePort()
        {
            Process[] candidates = Process.GetProcessesByName("RocketLeague");

            // search cmd line args for port
            foreach (var candidate in candidates)
            {
                string[] args = GetProcessArgs(candidate);

                foreach (var arg in args)
                {
                    if (arg.Contains("RLBot_ControllerURL"))
                    {
                        string[] parts = arg.Split(':');
                        var port = parts[parts.Length - 1].TrimEnd('"');
                        return int.Parse(port);
                    }
                }
            }

            for (int portToTest = IdealGamePort; portToTest < 65535; portToTest++)
            {
                if (portToTest == RLBotSocketsPort)
                {
                    // skip the port we're using for sockets
                    continue;
                }

                // try booting up a server on the port
                try
                {
                    TcpListener listener = new TcpListener(IPAddress.Any, portToTest);
                    listener.Start();
                    listener.Stop();
                    return portToTest;
                }
                catch (SocketException)
                {
                    continue;
                }
            }

            return DefaultGamePort;
        }

        public static string[] GetProcessArgs(Process process)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using (
                    var searcher = new System.Management.ManagementObjectSearcher(
                        $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}"
                    )
                )
                using (var objects = searcher.Get())
                {
                    return objects
                        .Cast<System.Management.ManagementBaseObject>()
                        .SingleOrDefault()
                        ?["CommandLine"]?.ToString()
                        .Split(" ");
                }
            }
            else
            {
                return process.StartInfo.Arguments.Split(' ');
            }
        }

        public static string[] GetIdealArgs(int gamePort)
        {
            return ["-rlbot", $"RLBot_ControllerURL=127.0.0.1:{gamePort}", "RLBot_PacketSendRate=240", "-nomovie"];
        }

        public static void LaunchBots(List<rlbot.flat.PlayerConfigurationT> players)
        {
            foreach (var player in players)
            {
                if (player.RunCommand == "")
                {
                    continue;
                }

                Process botProcess = new();

                if (player.Location != "")
                {
                    botProcess.StartInfo.WorkingDirectory = player.Location;
                }

                try
                {
                    string[] commandParts = player.RunCommand.Split(' ', 2);
                    botProcess.StartInfo.FileName = Path.Join(player.Location, commandParts[0]);
                    botProcess.StartInfo.Arguments = commandParts[1];

                    botProcess.StartInfo.EnvironmentVariables["BOT_SPAWN_ID"] = player.SpawnId.ToString();

                    botProcess.Start();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Failed to launch bot {player.Name}: {e.Message}");
                }
            }
        }

        public static void LaunchScripts(List<rlbot.flat.ScriptConfigurationT> scripts)
        {
            foreach (var script in scripts)
            {
                if (script.RunCommand == "")
                {
                    continue;
                }

                Process scriptProcess = new();

                if (script.Location != "")
                {
                    scriptProcess.StartInfo.WorkingDirectory = script.Location;
                }

                try
                {
                    string[] commandParts = script.RunCommand.Split(' ', 2);
                    scriptProcess.StartInfo.FileName = Path.Join(script.Location, commandParts[0]);
                    scriptProcess.StartInfo.Arguments = commandParts[1];

                    scriptProcess.Start();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Failed to launch script: {e.Message}");
                }
            }
        }

        public static void LaunchRocketLeague(rlbot.flat.Launcher launcher, int gamePort)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                switch (launcher)
                {
                    case rlbot.flat.Launcher.Steam:
                        Process rocketLeague = new();

                        string steamPath = GetSteamPath();
                        rocketLeague.StartInfo.FileName = steamPath;
                        rocketLeague.StartInfo.Arguments =
                            $"-applaunch {SteamGameId} " + string.Join(" ", GetIdealArgs(gamePort));

                        Console.WriteLine(
                            $"Starting Rocket League with args {steamPath} {rocketLeague.StartInfo.Arguments}"
                        );
                        rocketLeague.Start();
                        break;
                    case rlbot.flat.Launcher.Epic:
                        break;
                    case rlbot.flat.Launcher.Custom:
                        break;
                    default:
                        break;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                switch (launcher)
                {
                    case rlbot.flat.Launcher.Steam:
                        Process rocketLeague = new();
                        rocketLeague.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

                        string args = string.Join("%20", GetIdealArgs(gamePort));
                        rocketLeague.StartInfo.FileName = "steam";
                        rocketLeague.StartInfo.Arguments = $"steam://rungameid/{SteamGameId}//{args}";

                        Console.WriteLine(
                            $"Starting Rocket League via Steam CLI with {rocketLeague.StartInfo.Arguments}"
                        );
                        rocketLeague.Start();
                        break;
                    case rlbot.flat.Launcher.Epic:
                        break;
                    case rlbot.flat.Launcher.Custom:
                        break;
                    default:
                        break;
                }
            }
        }

        public static bool IsRocketLeagueRunning()
        {
            Process[] candidates = Process.GetProcesses();

            foreach (var candidate in candidates)
            {
                if (candidate.ProcessName.Contains("RocketLeague"))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetSteamPath()
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key != null)
            {
                return key.GetValue("SteamExe").ToString();
            }
            else
            {
                throw new FileNotFoundException(
                    "Could not find registry entry for SteamExe... Is Steam installed?"
                );
            }
        }
    }
}
