using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WindowsGSM.Functions;

namespace WindowsGSM.DiscordBot
{
    class Commands
    {
        private readonly DiscordSocketClient _client;
        private static readonly SemaphoreSlim _dynamicMessageLock = new SemaphoreSlim(1, 1);
        private static string _lastDynamicContent = null;
        private static ulong? _lastDynamicChannelId = null;
        private static ulong? _lastDynamicMessageId = null;
        private static readonly string LOG_DIR = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "debug");
        private static readonly bool ENABLE_LOG = true;

        public Commands(DiscordSocketClient client)
        {
            _client = client;
            _client.MessageReceived += CommandReceivedAsync;
            Log("[BOT] Commands initialized");
        }

        private static string GetLogFilePath()
        {
            Directory.CreateDirectory(LOG_DIR);
            return Path.Combine(LOG_DIR, $"debug_{DateTime.Now:yyyy-MM-dd}.log");
        }

        private static void Log(string message)
        {
        #if DEBUG
                    bool allow = true;
        #else
            bool allow = ENABLE_LOG;
        #endif

            if (!allow)
                return;

            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                Debug.WriteLine(line);
                File.AppendAllText(GetLogFilePath(), line + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static void LogError(string context, Exception ex)
        {
            Log($"[ERROR] {context} | {ex}");
        }

        private static string GetStatusEmoji(string status)
        {
            return status switch
            {
                "Started" => "🟢",
                "Starting" => "🟡",
                "Stopped" => "🔴",
                "Stopping" => "🟠",
                "Updating" => "🔵",
                "Backuping" => "🟣",
                _ => "⚪"
            };
        }

        private static string BuildLine(string id, string status, string name)
        {
            return $"🆔 {id} | {GetStatusEmoji(status)} {status} | 🎮 {name}";
        }

        private bool IsTrackedDynamicMessage(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            content = content.Trim();

            return content.Contains("🆔")
                || content.StartsWith("all server offline.")
                || content.StartsWith("Server name");
        }

        private async Task<IUserMessage> GetDynamicStatusMessageAsync(ISocketMessageChannel channel)
        {
            try
            {
                Log("[BOT] GetDynamicStatusMessageAsync BEGIN");

                // 1) Essayer d'abord avec l'ID mémorisé
                if (_lastDynamicMessageId.HasValue)
                {
                    try
                    {
                        var cached = await channel.GetMessageAsync(_lastDynamicMessageId.Value);
                        if (cached is IUserMessage cachedUserMessage &&
                            cachedUserMessage.Author.Id == _client.CurrentUser.Id &&
                            IsTrackedDynamicMessage(cachedUserMessage.Content))
                        {
                            Log($"[BOT] GetDynamicStatusMessageAsync FOUND BY ID id={cachedUserMessage.Id} content={cachedUserMessage.Content?.Replace("\n", " | ")}");
                            return cachedUserMessage;
                        }

                        Log("[BOT] GetDynamicStatusMessageAsync cached message invalid, fallback to scan");
                    }
                    catch (Exception ex)
                    {
                        LogError("GetDynamicStatusMessageAsync GetMessageAsync by ID failed", ex);
                    }
                }

                // 2) Fallback: scan des derniers messages
                var messages = await channel.GetMessagesAsync(100).FlattenAsync();

                var tracked = messages
                    .OfType<IUserMessage>()
                    .Where(m => m.Author.Id == _client.CurrentUser.Id)
                    .Where(m => IsTrackedDynamicMessage(m.Content))
                    .OrderByDescending(m => m.Timestamp)
                    .FirstOrDefault();

                if (tracked != null)
                {
                    _lastDynamicMessageId = tracked.Id;
                    Log($"[BOT] GetDynamicStatusMessageAsync FOUND BY SCAN id={tracked.Id} content={tracked.Content?.Replace("\n", " | ")}");
                }
                else
                {
                    Log("[BOT] GetDynamicStatusMessageAsync FOUND nothing");
                }

                return tracked;
            }
            catch (Exception ex)
            {
                LogError("GetDynamicStatusMessageAsync failed", ex);
                return null;
            }
        }

        private async Task<string> BuildDynamicServerListContentAsync(
            string keepStoppedOnlyServerId = null,
            HashSet<string> keepStoppedServerIds = null)
        {
            string content = "Aucun serveur actif.";

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                var list = WindowsGSM.GetServerList();

                Log($"[LIST] GetServerList count={list.Count}");

                var finalList = list
                    .Where(x =>
                        x.Item2.Equals("Started", StringComparison.OrdinalIgnoreCase) ||
                        x.Item2.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
                        x.Item2.Equals("Stopping", StringComparison.OrdinalIgnoreCase) ||
                        x.Item2.Equals("Updating", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(keepStoppedOnlyServerId) &&
                         x.Item1 == keepStoppedOnlyServerId &&
                         x.Item2.Equals("Stopped", StringComparison.OrdinalIgnoreCase)) ||
                        (keepStoppedServerIds != null &&
                         keepStoppedServerIds.Contains(x.Item1) &&
                         x.Item2.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
                    )
                    .OrderBy(x => int.TryParse(x.Item1, out var id) ? id : int.MaxValue)
                    .ToList();

                foreach (var x in finalList)
                {
                    Log($"[LIST] FINAL id={x.Item1} status={x.Item2} name={x.Item3}");
                }

                content = finalList.Count == 0
                    ? "Aucun serveur actif."
                    : string.Join("\n", finalList.Select(x => BuildLine(x.Item1, x.Item2, x.Item3)));
            });

            Log($"[LIST] CONTENT={content.Replace("\n", " | ")}");
            return content;
        }

        private async Task SetDynamicStatusMessageAsync(SocketMessage triggerMessage, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return;

            content = content.Trim();

            if (triggerMessage?.Channel != null)
            {
                if (_lastDynamicChannelId != triggerMessage.Channel.Id)
                {
                    _lastDynamicChannelId = triggerMessage.Channel.Id;
                    _lastDynamicMessageId = null;
                    _lastDynamicContent = null;
                }
            }

            if (string.Equals(_lastDynamicContent, content, StringComparison.Ordinal))
            {
                Log("[BOT] SetDynamicStatusMessageAsync SKIP same as last content cache");
                return;
            }

            Log($"[BOT] SetDynamicStatusMessageAsync REQUEST content={content.Replace("\n", " | ")}");

            await _dynamicMessageLock.WaitAsync();
            try
            {
                if (string.Equals(_lastDynamicContent, content, StringComparison.Ordinal))
                {
                    Log("[BOT] SetDynamicStatusMessageAsync SKIP same as last content cache (inside lock)");
                    return;
                }

                var trackedMessage = await GetDynamicStatusMessageAsync(triggerMessage.Channel);

                if (trackedMessage != null)
                {
                    try
                    {
                        if (string.Equals(trackedMessage.Content?.Trim(), content, StringComparison.Ordinal))
                        {
                            _lastDynamicContent = content;
                            Log($"[BOT] SetDynamicStatusMessageAsync SKIP same as tracked message id={trackedMessage.Id}");
                            return;
                        }

                        Log($"[BOT] SetDynamicStatusMessageAsync MODIFY id={trackedMessage.Id}");
                        await trackedMessage.ModifyAsync(m =>
                        {
                            m.Content = content;
                            m.Embeds = Array.Empty<Embed>();
                        });

                        _lastDynamicContent = content;
                        _lastDynamicMessageId = trackedMessage.Id;
                        Log($"[BOT] SetDynamicStatusMessageAsync MODIFY OK id={trackedMessage.Id}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogError($"SetDynamicStatusMessageAsync MODIFY failed id={trackedMessage.Id}", ex);
                    }
                }

                Log("[BOT] SetDynamicStatusMessageAsync SEND NEW");
                var sent = await triggerMessage.Channel.SendMessageAsync(content);

                if (sent != null)
                {
                    _lastDynamicContent = content;
                    _lastDynamicMessageId = sent.Id;
                    Log($"[BOT] SetDynamicStatusMessageAsync SEND OK id={sent.Id}");
                }
            }
            finally
            {
                _dynamicMessageLock.Release();
            }
        }

        private async Task RefreshDynamicServerListAsync(
            SocketMessage triggerMessage,
            string keepStoppedOnlyServerId = null)
        {
            Log($"[LIST] RefreshDynamicServerListAsync BEGIN keepStoppedOnlyServerId={keepStoppedOnlyServerId ?? "null"}");

            string content = await BuildDynamicServerListContentAsync(
                keepStoppedOnlyServerId: keepStoppedOnlyServerId
            );

            await SetDynamicStatusMessageAsync(triggerMessage, content);
        }

        private async Task RefreshDynamicServerListAsync(
            SocketMessage triggerMessage,
            HashSet<string> keepStoppedServerIds)
        {
            Log($"[LIST] RefreshDynamicServerListAsync(BATCH) BEGIN keepStoppedServerIds={(keepStoppedServerIds == null ? "null" : string.Join(",", keepStoppedServerIds))}");

            string content = await BuildDynamicServerListContentAsync(
                keepStoppedServerIds: keepStoppedServerIds
            );

            await SetDynamicStatusMessageAsync(triggerMessage, content);
        }

        public async Task RefreshDynamicServerListFromLastChannelAsync(string keepStoppedOnlyServerId = null)
        {
            try
            {
                if (!_lastDynamicChannelId.HasValue)
                {
                    Log("[BOT] RefreshDynamicServerListFromLastChannelAsync SKIP no saved channel");
                    return;
                }

                var channel = _client.GetChannel(_lastDynamicChannelId.Value) as ISocketMessageChannel;
                if (channel == null)
                {
                    Log($"[BOT] RefreshDynamicServerListFromLastChannelAsync SKIP channel not found id={_lastDynamicChannelId.Value}");
                    return;
                }

                string content = await BuildDynamicServerListContentAsync(
                    keepStoppedOnlyServerId: keepStoppedOnlyServerId
                );

                if (string.Equals(_lastDynamicContent, content, StringComparison.Ordinal))
                {
                    Log("[BOT] RefreshDynamicServerListFromLastChannelAsync SKIP same as last content cache");
                    return;
                }

                await _dynamicMessageLock.WaitAsync();
                try
                {
                    var trackedMessage = await GetDynamicStatusMessageAsync(channel);

                    if (trackedMessage != null)
                    {
                        if (string.Equals(trackedMessage.Content?.Trim(), content, StringComparison.Ordinal))
                        {
                            _lastDynamicContent = content;
                            _lastDynamicMessageId = trackedMessage.Id;
                            Log($"[BOT] RefreshDynamicServerListFromLastChannelAsync MODIFY OK id={trackedMessage.Id}");
                            return;
                        }

                        await trackedMessage.ModifyAsync(m =>
                        {
                            m.Content = content;
                            m.Embeds = Array.Empty<Embed>();
                        });

                        _lastDynamicContent = content;
                        _lastDynamicMessageId = trackedMessage.Id;
                        Log($"[BOT] RefreshDynamicServerListFromLastChannelAsync MODIFY OK id={trackedMessage.Id}");
                    }
                    else
                    {
                        var sent = await channel.SendMessageAsync(content);
                        if (sent != null)
                        {
                            _lastDynamicContent = content;
                            _lastDynamicMessageId = sent.Id;
                            Log($"[BOT] RefreshDynamicServerListFromLastChannelAsync SEND OK id={sent.Id}");
                        }
                    }
                }
                finally
                {
                    _dynamicMessageLock.Release();
                }
            }
            catch (Exception ex)
            {
                LogError("RefreshDynamicServerListFromLastChannelAsync failed", ex);
            }
        }

        private async Task WatchAndRefreshServerStatusAsync(
            SocketMessage triggerMessage,
            string serverId,
            bool keepStoppedLine = false,
            int maxLoops = 120,
            int delayMs = 500)
        {
            Log($"[WATCH] BEGIN serverId={serverId} keepStoppedLine={keepStoppedLine} maxLoops={maxLoops} delayMs={delayMs}");

            MainWindow.ServerStatus lastSeenStatus = (MainWindow.ServerStatus)(-1);
            bool completed = false;
            bool sawStartTransition = false;
            bool didFinalRefreshInsideLoop = false;
            int stableStartedCount = 0;

            for (int i = 0; i < maxLoops; i++)
            {
                MainWindow.ServerStatus status = MainWindow.ServerStatus.Stopped;
                string serverName = serverId;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                    if (WindowsGSM.IsServerExist(serverId))
                    {
                        status = WindowsGSM.GetServerStatus(serverId);
                        serverName = WindowsGSM.GetServerName(serverId);
                    }
                });

                Log($"[WATCH] LOOP={i} serverId={serverId} status={status} lastSeenStatus={lastSeenStatus} sawStartTransition={sawStartTransition}");

                if (!keepStoppedLine)
                {
                    if (status == MainWindow.ServerStatus.Starting ||
                        status == MainWindow.ServerStatus.Updating)
                    {
                        sawStartTransition = true;
                    }
                }

                if (status == MainWindow.ServerStatus.Started)
                    stableStartedCount++;
                else
                    stableStartedCount = 0;

                if (status != lastSeenStatus)
                {
                    Log($"[WATCH] STATUS CHANGE serverId={serverId} {lastSeenStatus} -> {status}");

                    if (keepStoppedLine && status == MainWindow.ServerStatus.Stopped)
                    {
                        Log($"[WATCH] REFRESH STOPPED WITH FULL LIST serverId={serverId} name={serverName}");

                        await RefreshDynamicServerListAsync(
                            triggerMessage,
                            keepStoppedOnlyServerId: serverId
                        );

                        await Task.Delay(700);
                        await RefreshDynamicServerListAsync(triggerMessage);

                        lastSeenStatus = status;
                        completed = true;
                        didFinalRefreshInsideLoop = true;
                        break;
                    }

                    await RefreshDynamicServerListAsync(
                        triggerMessage,
                        keepStoppedOnlyServerId: keepStoppedLine ? serverId : null
                    );

                    lastSeenStatus = status;
                }

                if (!keepStoppedLine)
                {
                    if (status == MainWindow.ServerStatus.Started && stableStartedCount >= 2)
                    {
                        Log($"[WATCH] STABLE STARTED serverId={serverId}");
                        completed = true;
                        didFinalRefreshInsideLoop = true;
                        break;
                    }

                    if (status == MainWindow.ServerStatus.Stopped && sawStartTransition)
                    {
                        Log($"[WATCH] BREAK stopped after transition serverId={serverId}");
                        await RefreshDynamicServerListAsync(triggerMessage);
                        completed = true;
                        didFinalRefreshInsideLoop = true;
                        break;
                    }

                    if (status == MainWindow.ServerStatus.Stopped && !sawStartTransition)
                    {
                        Log($"[WATCH] IGNORE early stopped serverId={serverId}");
                    }
                }
                else
                {
                    if (status == MainWindow.ServerStatus.Stopped)
                    {
                        Log($"[WATCH] BREAK stopped serverId={serverId}");
                        completed = true;
                        didFinalRefreshInsideLoop = true;
                        break;
                    }
                }

                await Task.Delay(delayMs);
            }

            if (!didFinalRefreshInsideLoop && lastSeenStatus != (MainWindow.ServerStatus)(-1))
            {
                await RefreshDynamicServerListAsync(
                    triggerMessage,
                    keepStoppedOnlyServerId: keepStoppedLine ? serverId : null
                );
            }

            Log($"[WATCH] END serverId={serverId} completed={completed} sawStartTransition={sawStartTransition} didFinalRefreshInsideLoop={didFinalRefreshInsideLoop}");
        }

        private async Task SendTemporaryMessage(SocketMessage triggerMessage, string content)
        {
            Log($"[BOT] SendTemporaryMessage content={content.Replace("\n", " | ")}");

            var msg = await triggerMessage.Channel.SendMessageAsync(content);
            if (msg != null)
            {
                Log($"[BOT] SendTemporaryMessage sent id={msg.Id}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(5000);
                        await msg.DeleteAsync();
                        Log($"[BOT] SendTemporaryMessage deleted id={msg.Id}");
                    }
                    catch (Exception ex)
                    {
                        LogError("SendTemporaryMessage delete failed", ex);
                    }
                });
            }
        }

        private async Task CommandReceivedAsync(SocketMessage message)
        {
            Log($"[CMD] MessageReceived author={message.Author.Id} content={message.Content}");

            if (message.Author.Id == _client.CurrentUser.Id) { return; }

            List<string> adminIds = Configs.GetBotAdminIds();
            if (!adminIds.Contains(message.Author.Id.ToString()))
            {
                Log($"[CMD] Rejected non-admin author={message.Author.Id}");
                return;
            }

            var prefix = Configs.GetBotPrefix();
            var commandLen = prefix.Length + 4;
            if (message.Content.Length < commandLen) { return; }

            if (message.Content.Length == commandLen && message.Content.ToLower().Trim() == $"{prefix}wgsm".ToLower().Trim())
            {
                Log("[CMD] Help requested");
                await SendHelpEmbed(message);
                return;
            }

            if (message.Content.Length >= commandLen + 1 &&
                message.Content.Substring(0, commandLen + 1).ToLower().Trim() == $"{prefix}wgsm ".ToLower().Trim())
            {
                string[] args = message.Content.Split(new[] { ' ' }, 2);
                string[] splits = args[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (splits.Length == 0)
                {
                    Log("[CMD] Empty splits -> help");
                    await SendHelpEmbed(message);
                    return;
                }

                string command = splits[0].Trim().ToLower();
                Log($"[CMD] Parsed command={command}");

                switch (command)
                {
                    case "start":
                    case "stop":
                    case "stopall":
                    case "restart":
                    case "send":
                    case "sendr":
                    case "list":
                    case "check":
                    case "backup":
                    case "update":
                    case "stats":
                    case "players":
                    case "serverstats":
                        List<string> serverIds = Configs.GetServerIdsByAdminId(message.Author.Id.ToString());

                        if (command == "check")
                        {
                            Log("[CMD] Action check");
                            await message.Channel.SendMessageAsync(
                                serverIds.Contains("0")
                                ? "You have full permission.\nCommands: `check`, `list`, `start`, `stop`, `stopAll`, `restart`, `send`, `sendR`, `backup`, `update`, `players`, `stats`"
                                : $"You have permission on servers (`{string.Join(",", serverIds.ToArray())}`)\nCommands: `check`, `start`, `stop`, `restart`, `send`, `sendR`, `backup`, `update`, `players`, `stats`");
                            break;
                        }

                        if (command == "list" && serverIds.Contains("0"))
                        {
                            await Action_List(message);
                        }
                        else if (command == "stopall" && serverIds.Contains("0"))
                        {
                            await Action_StopAll(message);
                        }
                        else if (command == "stats")
                        {
                            await Action_Stats(message);
                        }
                        else if (command != "list" && command != "stopall" && command != "stats" &&
                                 splits.Length > 1 &&
                                 (serverIds.Contains("0") || serverIds.Contains(splits[1])))
                        {
                            switch (command)
                            {
                                case "start": await Action_Start(message, args[1]); break;
                                case "stop": await Action_Stop(message, args[1]); break;
                                case "restart": await Action_Restart(message, args[1]); break;
                                case "send": await Action_SendCommand(message, args[1]); break;
                                case "sendr": await Action_SendCommand(message, args[1], true); break;
                                case "backup": await Action_Backup(message, args[1]); break;
                                case "update": await Action_Update(message, args[1]); break;
                                case "players": await Action_PlayerList(message, args[1]); break;
                                case "serverstats": await Action_GameServerStats(message, args[1]); break;
                            }
                        }
                        else
                        {
                            Log($"[CMD] Permission denied command={command} raw={message.Content}");
                            await message.Channel.SendMessageAsync("You don't have permission to access.");
                        }
                        break;

                    default:
                        Log($"[CMD] Unknown command={command} -> help");
                        await SendHelpEmbed(message);
                        break;
                }
            }
        }

        private async Task Action_PlayerList(SocketMessage message, string command)
        {
            Log($"[CMD] Action_PlayerList RAW command={command}");

            var embed = new EmbedBuilder { };
            string[] args = command.Split(' ');
            if (args.Length >= 2 && int.TryParse(args[1], out int i))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                    if (WindowsGSM.IsServerExist(args[1]))
                    {
                        var playerList = WindowsGSM.GetServerTableById(args[1]).PlayerList;
                        foreach (var player in playerList)
                        {
                            embed.AddField($"Player {player.Id}", $"Name: {player.Name}; Score:{player.Score}; Connected for:{player.TimeConnected?.TotalMinutes.ToString("#.##")}", inline: true);
                        }
                    }

                    if (embed.Fields.Count == 0)
                    {
                        embed.AddField("PlayerData", "No playerdata currently available!");
                    }
                });
            }
            else
            {
                embed.AddField("PlayerData", "Something went wrong in your query!");
            }

            await message.Channel.SendMessageAsync(embed: embed.Build());
        }

        private async Task Action_List(SocketMessage message)
        {
            Log("[CMD] Action_List");
            await RefreshDynamicServerListAsync(message);
        }

        private async Task Action_Start(SocketMessage message, string command)
        {
            Log($"[CMD] Action_Start RAW command={command}");

            string[] args = command.Split(' ');
            if (args.Length == 2 && int.TryParse(args[1], out int i))
            {
                MainWindow WindowsGSM = null;
                MainWindow.ServerStatus serverStatus = MainWindow.ServerStatus.Stopped;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    WindowsGSM = (MainWindow)Application.Current.MainWindow;
                    if (WindowsGSM.IsServerExist(args[1]))
                    {
                        serverStatus = WindowsGSM.GetServerStatus(args[1]);
                    }
                });

                Log($"[CMD] Action_Start id={args[1]} exists={(WindowsGSM != null && WindowsGSM.IsServerExist(args[1]))} status={serverStatus}");

                if (WindowsGSM == null || !WindowsGSM.IsServerExist(args[1]))
                {
                    Log($"[CMD] Action_Start server not found id={args[1]}");
                    await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) does not exists.");
                    return;
                }

                if (serverStatus == MainWindow.ServerStatus.Stopped)
                {
                    Log($"[CMD] Action_Start launching watcher+start id={args[1]}");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Application.Current.Dispatcher.InvokeAsync(async () =>
                            {
                                MainWindow wgsm = (MainWindow)Application.Current.MainWindow;
                                Log($"[CMD] StartServerById BEGIN id={args[1]}");
                                await wgsm.StartServerById(args[1], message.Author.Id.ToString(), message.Author.Username);
                                Log($"[CMD] StartServerById END id={args[1]}");
                            });
                        }
                        catch (Exception ex)
                        {
                            LogError($"StartServerById failed id={args[1]}", ex);
                        }
                    });

                    await WatchAndRefreshServerStatusAsync(message, args[1], keepStoppedLine: false);
                }
                else
                {
                    Log($"[CMD] Action_Start skipped refresh only id={args[1]} status={serverStatus}");
                    await RefreshDynamicServerListAsync(message);
                }
            }
            else
            {
                Log($"[CMD] Action_Start invalid usage command={command}");
                await message.Channel.SendMessageAsync($"Usage: {Configs.GetBotPrefix()}wgsm start `<SERVERID>`");
            }
        }

        private async Task Action_Stop(SocketMessage message, string command)
        {
            Log($"[CMD] Action_Stop RAW command={command}");

            string[] args = command.Split(' ');
            if (args.Length == 2 && int.TryParse(args[1], out int i))
            {
                MainWindow WindowsGSM = null;
                MainWindow.ServerStatus serverStatus = MainWindow.ServerStatus.Stopped;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    WindowsGSM = (MainWindow)Application.Current.MainWindow;
                    if (WindowsGSM.IsServerExist(args[1]))
                    {
                        serverStatus = WindowsGSM.GetServerStatus(args[1]);
                    }
                });

                Log($"[CMD] Action_Stop id={args[1]} exists={(WindowsGSM != null && WindowsGSM.IsServerExist(args[1]))} status={serverStatus}");

                if (WindowsGSM == null || !WindowsGSM.IsServerExist(args[1]))
                {
                    Log($"[CMD] Action_Stop server not found id={args[1]}");
                    await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) does not exists.");
                    return;
                }

                if (serverStatus == MainWindow.ServerStatus.Started || serverStatus == MainWindow.ServerStatus.Starting)
                {
                    Log($"[CMD] Action_Stop launching watcher+stop id={args[1]}");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Application.Current.Dispatcher.InvokeAsync(async () =>
                            {
                                MainWindow wgsm = (MainWindow)Application.Current.MainWindow;
                                Log($"[CMD] StopServerById BEGIN id={args[1]}");
                                await wgsm.StopServerById(args[1], message.Author.Id.ToString(), message.Author.Username);
                                Log($"[CMD] StopServerById END id={args[1]}");
                            });
                        }
                        catch (Exception ex)
                        {
                            LogError($"StopServerById failed id={args[1]}", ex);
                        }
                    });

                    await WatchAndRefreshServerStatusAsync(message, args[1], keepStoppedLine: true);
                }
                else
                {
                    Log($"[CMD] Action_Stop skipped refresh only id={args[1]} status={serverStatus}");
                    await RefreshDynamicServerListAsync(message, keepStoppedOnlyServerId: args[1]);
                }
            }
            else
            {
                Log($"[CMD] Action_Stop invalid usage command={command}");
                await message.Channel.SendMessageAsync($"Usage: {Configs.GetBotPrefix()}wgsm stop `<SERVERID>`");
            }
        }

        private async Task Action_StopAll(SocketMessage message)
        {
            Log("[CMD] Action_StopAll");

            List<string> serverIds = new List<string>();
            HashSet<string> requestedServerIds = new HashSet<string>();
            HashSet<string> newlyStoppedIds = new HashSet<string>();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                serverIds = WindowsGSM.GetServerList()
                    .OrderBy(x => int.TryParse(x.Item1, out var id) ? id : int.MaxValue)
                    .Select(x => x.Item1)
                    .ToList();
            });

            foreach (string serverId in serverIds)
            {
                bool exists = false;
                MainWindow.ServerStatus statusBefore = MainWindow.ServerStatus.Stopped;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;

                    exists = WindowsGSM.IsServerExist(serverId);
                    if (exists)
                    {
                        statusBefore = WindowsGSM.GetServerStatus(serverId);
                    }
                });

                if (!exists)
                    continue;

                Log($"[CMD] Action_StopAll serverId={serverId} statusBefore={statusBefore}");

                if (statusBefore == MainWindow.ServerStatus.Stopped)
                    continue;

                try
                {
                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                        await WindowsGSM.StopServerById(serverId, message.Author.Id.ToString(), message.Author.Username);
                    });

                    requestedServerIds.Add(serverId);
                    Log($"[CMD] Action_StopAll stop requested id={serverId}");
                }
                catch (Exception ex)
                {
                    LogError($"Action_StopAll StopServerById failed id={serverId}", ex);
                }
            }

            await RefreshDynamicServerListAsync(message);

            if (requestedServerIds.Count == 0)
            {
                Log("[CMD] Action_StopAll no action needed");
                await RefreshDynamicServerListAsync(message);
                return;
            }

            const int maxLoops = 240;
            const int delayMs = 500;

            for (int loop = 0; loop < maxLoops; loop++)
            {
                int activeOrTransitionCount = 0;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MainWindow wgsm = (MainWindow)Application.Current.MainWindow;
                    var list = wgsm.GetServerList();

                    foreach (var srv in list)
                    {
                        string id = srv.Item1;
                        string status = srv.Item2;

                        if (!requestedServerIds.Contains(id))
                            continue;

                        Log($"[STOPALL-WATCH] loop={loop} id={id} status={status}");

                        if (status.Equals("Started", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("Stopping", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("Updating", StringComparison.OrdinalIgnoreCase))
                        {
                            activeOrTransitionCount++;
                        }

                        if (status.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
                        {
                            newlyStoppedIds.Add(id);
                        }
                    }
                });

                if (newlyStoppedIds.Count > 0)
                    await RefreshDynamicServerListAsync(message, keepStoppedServerIds: newlyStoppedIds);

                Log($"[STOPALL-WATCH] loop={loop} activeOrTransitionCount={activeOrTransitionCount} newlyStoppedIds={string.Join(",", newlyStoppedIds)}");

                if (activeOrTransitionCount == 0)
                {
                    Log("[STOPALL-WATCH] all requested servers are stopped");
                    break;
                }

                await Task.Delay(delayMs);
            }

            await RefreshDynamicServerListAsync(message); // final = no more keepStopped

        }

        private async Task Action_Restart(SocketMessage message, string command)
        {
            Log($"[CMD] Action_Restart RAW command={command}");

            string[] args = command.Split(' ');
            if (args.Length == 2 && int.TryParse(args[1], out int i))
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                    if (WindowsGSM.IsServerExist(args[1]))
                    {
                        MainWindow.ServerStatus serverStatus = WindowsGSM.GetServerStatus(args[1]);
                        Log($"[CMD] Action_Restart id={args[1]} status={serverStatus}");

                        if (serverStatus == MainWindow.ServerStatus.Started || serverStatus == MainWindow.ServerStatus.Starting)
                        {
                            await WindowsGSM.RestartServerById(args[1], message.Author.Id.ToString(), message.Author.Username);
                            Log($"[CMD] Action_Restart RestartServerById OK id={args[1]}");
                        }

                        await SendTemporaryMessage(
                            message,
                            BuildLine(args[1], WindowsGSM.GetServerStatus(args[1]).ToString(), WindowsGSM.GetServerName(args[1])));
                    }
                    else
                    {
                        Log($"[CMD] Action_Restart server not found id={args[1]}");
                        await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) does not exists.");
                    }
                });
            }
            else
            {
                Log($"[CMD] Action_Restart invalid usage command={command}");
                await message.Channel.SendMessageAsync($"Usage: {Configs.GetBotPrefix()}wgsm restart `<SERVERID>`");
            }
        }

        private async Task Action_SendCommand(SocketMessage message, string command, bool withResponse = false)
        {
            Log($"[CMD] Action_SendCommand RAW command={command} withResponse={withResponse}");

            string[] args = command.Split(' ');
            if (args.Length >= 2 && int.TryParse(args[1], out int id))
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                    if (WindowsGSM.IsServerExist(args[1]))
                    {
                        MainWindow.ServerStatus serverStatus = WindowsGSM.GetServerStatus(args[1]);
                        Log($"[CMD] Action_SendCommand id={args[1]} status={serverStatus}");

                        if (serverStatus == MainWindow.ServerStatus.Started || serverStatus == MainWindow.ServerStatus.Starting)
                        {
                            string sendCommand = command.Substring(args[1].Length + 6).Trim();
                            Log($"[CMD] Action_SendCommand sendCommand={sendCommand}");

                            var response = await WindowsGSM.SendCommandById(args[1], sendCommand, message.Author.Id.ToString(), message.Author.Username, withResponse ? 1000 : 0);

                            await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) {(!string.IsNullOrWhiteSpace(response) ? "Command sent" : "Fail to send command")}. | `{sendCommand}`");

                            if (withResponse)
                            {
                                await SendMultiLog(message, response);
                            }
                        }
                        else
                        {
                            await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) currently in {serverStatus} state, not able to send command.");
                        }
                    }
                    else
                    {
                        Log($"[CMD] Action_SendCommand server not found id={args[1]}");
                        await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) does not exists.");
                    }
                });
            }
            else
            {
                Log($"[CMD] Action_SendCommand invalid usage command={command}");
                await message.Channel.SendMessageAsync($"Usage: {Configs.GetBotPrefix()}wgsm send `<SERVERID>` `<COMMAND>`");
            }
        }

        public static async Task SendMultiLog(SocketMessage message, string response)
        {
            Log("[BOT] SendMultiLog BEGIN");

            await message.Channel.SendMessageAsync("LastLog:");
            const int signsToSend = 1800;
            for (int i = 0; i < response.Length; i += signsToSend)
            {
                var len = i + signsToSend < response.Length ? signsToSend : response.Length - i;
                await message.Channel.SendMessageAsync($"```\n{response.Substring(i, len)}\n```");
            }

            Log("[BOT] SendMultiLog END");
        }

        private async Task Action_Backup(SocketMessage message, string command)
        {
            Log($"[CMD] Action_Backup RAW command={command}");

            string[] args = command.Split(' ');
            if (args.Length >= 2 && int.TryParse(args[1], out int i))
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                    if (WindowsGSM.IsServerExist(args[1]))
                    {
                        MainWindow.ServerStatus serverStatus = WindowsGSM.GetServerStatus(args[1]);
                        Log($"[CMD] Action_Backup id={args[1]} status={serverStatus}");

                        if (serverStatus == MainWindow.ServerStatus.Stopped)
                        {
                            await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) Backup started - this may take some time.");
                            await WindowsGSM.BackupServerById(args[1], message.Author.Id.ToString(), message.Author.Username);
                        }
                        else if (serverStatus == MainWindow.ServerStatus.Backuping)
                        {
                            await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) already Backuping.");
                        }
                        else
                        {
                            await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) currently in {serverStatus} state, not able to backup.");
                        }

                        await SendTemporaryMessage(
                            message,
                            BuildLine(args[1], WindowsGSM.GetServerStatus(args[1]).ToString(), WindowsGSM.GetServerName(args[1])));
                    }
                    else
                    {
                        Log($"[CMD] Action_Backup server not found id={args[1]}");
                        await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) does not exists.");
                    }
                });
            }
            else
            {
                Log($"[CMD] Action_Backup invalid usage command={command}");
                await message.Channel.SendMessageAsync($"Usage: {Configs.GetBotPrefix()}wgsm backup `<SERVERID>`");
            }
        }

        private async Task Action_Update(SocketMessage message, string command)
        {
            Log($"[CMD] Action_Update RAW command={command}");

            string[] args = command.Split(' ');
            if (args.Length >= 2 && int.TryParse(args[1], out int i))
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                    if (WindowsGSM.IsServerExist(args[1]))
                    {
                        MainWindow.ServerStatus serverStatus = WindowsGSM.GetServerStatus(args[1]);
                        Log($"[CMD] Action_Update id={args[1]} status={serverStatus}");

                        if (serverStatus == MainWindow.ServerStatus.Stopped)
                        {
                            await WindowsGSM.UpdateServerById(args[1], message.Author.Id.ToString(), message.Author.Username);
                        }
                        else if (serverStatus == MainWindow.ServerStatus.Updating)
                        {
                            Log($"[CMD] Action_Update already updating id={args[1]}");
                        }

                        await RefreshDynamicServerListAsync(message);
                    }
                    else
                    {
                        Log($"[CMD] Action_Update server not found id={args[1]}");
                        await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) does not exists.");
                    }
                });
            }
            else
            {
                Log($"[CMD] Action_Update invalid usage command={command}");
                await message.Channel.SendMessageAsync($"Usage: {Configs.GetBotPrefix()}wgsm update `<SERVERID>`");
            }
        }

        private async Task Action_Stats(SocketMessage message)
        {
            Log("[CMD] Action_Stats");

            var system = new SystemMetrics();
            await Task.Run(() => system.GetCPUStaticInfo());
            await Task.Run(() => system.GetRAMStaticInfo());
            await Task.Run(() => system.GetDiskStaticInfo());

            string statsMessage = await BuildStatsMessage(system);
            await SetDynamicStatusMessageAsync(message, statsMessage);
        }

        private async Task<string> BuildStatsMessage(SystemMetrics system)
        {
            double cpuUsage = await Task.Run(() => system.GetCPUUsage());
            double ramUsage = await Task.Run(() => system.GetRAMUsage());
            double diskUsage = await Task.Run(() => system.GetDiskUsage());

            (int serverCount, int startedCount, int activePlayers) = await GetGameServerDashBoardDetails();

            int serverPercent = (int)Math.Round(serverCount * 100.0 / MainWindow.MAX_SERVER);
            int onlinePercent = serverCount == 0 ? 0 : (int)Math.Round(startedCount * 100.0 / serverCount);

            string memoryRatio = SystemMetrics.GetMemoryRatioString(ramUsage, system.RAMTotalSize);
            string diskRatio = SystemMetrics.GetDiskRatioString(diskUsage, system.DiskTotalSize);

            string msg =
                $"Server name: {Environment.MachineName}\n" +
                $"CPU {Math.Round(cpuUsage)}% / Memory: {memoryRatio} {BuildBarLine(ramUsage)} {Math.Round(ramUsage)}% / Disk: {diskRatio} {BuildBarLine(diskUsage)} {Math.Round(diskUsage)}%\n" +
                $"Servers: {serverCount}/{MainWindow.MAX_SERVER} {BuildBarLine(serverPercent)} {serverPercent}% / Online: {startedCount}/{serverCount} {onlinePercent}% / Active Players: {activePlayers}";

            Log($"[BOT] BuildStatsMessage={msg.Replace("\n", " | ")}");

            return msg;
        }

        private string BuildBarLine(double percent)
        {
            const int width = 10;
            int filled = (int)Math.Round(percent / 100.0 * width);

            if (filled < 0) filled = 0;
            if (filled > width) filled = width;

            return new string('█', filled).PadRight(width, ' ');
        }

        private async Task Action_GameServerStats(SocketMessage message, string command)
        {
            Log($"[CMD] Action_GameServerStats RAW command={command}");

            string[] args = command.Split(' ');
            if (args.Length == 2 && int.TryParse(args[1], out int i))
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                    if (WindowsGSM.IsServerExist(args[1]))
                    {
                        MainWindow.ServerStatus serverStatus = WindowsGSM.GetServerStatus(args[1]);
                        Log($"[CMD] Action_GameServerStats id={args[1]} status={serverStatus}");

                        if (serverStatus == MainWindow.ServerStatus.Started || serverStatus == MainWindow.ServerStatus.Starting)
                        {
                            var serverTable = WindowsGSM.GetServerTableById(args[1]);
                            await message.Channel.SendMessageAsync(embed: (await GetServerStatsMessage(serverTable)).Build());
                        }
                        else
                        {
                            await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) currently in {serverStatus} state, not able to gather infos.");
                        }
                    }
                    else
                    {
                        Log($"[CMD] Action_GameServerStats server not found id={args[1]}");
                        await message.Channel.SendMessageAsync($"Server (ID: {args[1]}) does not exists.");
                    }
                });
            }
            else
            {
                Log($"[CMD] Action_GameServerStats invalid usage command={command}");
                await message.Channel.SendMessageAsync($"Usage: {Configs.GetBotPrefix()}wgsm serverstats `<SERVERID>`");
            }
        }

        private async Task<(int serverCount, int startedCount, int activePlayers)> GetGameServerDashBoardDetails()
        {
            int serverCount = 0;
            int startedCount = 0;
            int activePlayers = 0;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MainWindow WindowsGSM = (MainWindow)Application.Current.MainWindow;
                serverCount = WindowsGSM.ServerGrid.Items.Count;
                startedCount = WindowsGSM.GetStartedServerCount();
                activePlayers = WindowsGSM.GetActivePlayers();
            });

            return (serverCount, startedCount, activePlayers);
        }

        private async Task<EmbedBuilder> GetServerStatsMessage(ServerTable server)
        {
            var embed = new EmbedBuilder();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                embed.Title = $"Server Stats - {server.Name}";
                embed.AddField("ID", server.ID, true);
                embed.AddField("Game", server.Game, true);
                embed.AddField("Status", server.Status, true);
                embed.AddField("IP", server.IP, true);
                embed.AddField("Port", server.Port, true);
                embed.AddField("Query Port", server.QueryPort, true);
                embed.AddField("Map", server.Defaultmap, true);
                embed.AddField("Players", server.Maxplayers, true);
                embed.AddField("Uptime", string.IsNullOrWhiteSpace(server.Uptime) ? "N/A" : server.Uptime, true);
            });

            return embed;
        }

        private async Task SendHelpEmbed(SocketMessage message)
        {
            var embed = new EmbedBuilder()
                .WithTitle("WindowsGSM Discord Bot")
                .WithDescription("Available commands")
                .AddField($"{Configs.GetBotPrefix()}wgsm check", "Check your permissions", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm list", "Show active servers", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm start <SERVERID>", "Start a server", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm stop <SERVERID>", "Stop a server", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm stopall", "Stop all servers", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm restart <SERVERID>", "Restart a server", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm send <SERVERID> <COMMAND>", "Send command", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm sendr <SERVERID> <COMMAND>", "Send command with response", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm backup <SERVERID>", "Backup a server", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm update <SERVERID>", "Update a server", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm players <SERVERID>", "Show players", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm stats", "Show system stats", false)
                .AddField($"{Configs.GetBotPrefix()}wgsm serverstats <SERVERID>", "Show server stats", false);

            await message.Channel.SendMessageAsync(embed: embed.Build());
        }
    }
}