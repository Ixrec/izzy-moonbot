using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.Net;
using Discord.WebSocket;
using Izzy_Moonbot.Adapters;
using Izzy_Moonbot.Attributes;
using Izzy_Moonbot.EventListeners;
using Izzy_Moonbot.Helpers;
using Izzy_Moonbot.Modules;
using Izzy_Moonbot.Service;
using Izzy_Moonbot.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Serilog;

namespace Izzy_Moonbot
{
    public class Worker : BackgroundService
    {
        private readonly CommandService _commands;
        private readonly DiscordSettings _discordSettings;
        private readonly FilterService _filterService;
        private readonly ILogger<Worker> _logger;
        private readonly ModLoggingService _modLog;
        private readonly ModService _modService;
        private readonly SpamService _spamService;
        private readonly RaidService _raidService;
        private readonly ScheduleService _scheduleService;
        private readonly IServiceCollection _services;
        private readonly Config _config;
        private readonly TransientState _state;
        private readonly Dictionary<ulong, User> _users;
        private readonly QuoteService _quoteService;
        private readonly ConfigListener _configListener;
        private readonly UserListener _userListener;
        private readonly MessageListener _messageListener;
        private readonly MonitoringService _monitoringService;
        private DiscordSocketClient _client;
        public bool hasProgrammingSocks = true;
        public int LaserCount = 10;

        public Worker(ILogger<Worker> logger, ModLoggingService modLog, IServiceCollection services, ModService modService, RaidService raidService,
            FilterService filterService, ScheduleService scheduleService, IOptions<DiscordSettings> discordSettings,
            Config config, TransientState state, Dictionary<ulong, User> users, UserListener userListener, SpamService spamService, QuoteService quoteService,
            ConfigListener configListener, MessageListener messageListener, MonitoringService monitoringService)
        {
            _logger = logger;
            _modLog = modLog;
            _modService = modService;
            _raidService = raidService;
            _filterService = filterService;
            _scheduleService = scheduleService;
            var commandServiceConfig = new CommandServiceConfig();
            commandServiceConfig.CaseSensitiveCommands = false;
            _commands = new CommandService(commandServiceConfig);
            _discordSettings = discordSettings.Value;
            _services = services;
            _config = config;
            _state = state;
            _users = users;
            _userListener = userListener;
            _spamService = spamService;
            _quoteService = quoteService;
            _configListener = configListener;
            _messageListener = messageListener;
            _monitoringService = monitoringService;

            var discordConfig = new DiscordSocketConfig {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent,
                MessageCacheSize = 50
            };
            _client = new DiscordSocketClient(discordConfig);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _client.Log += Log;
                await _client.LoginAsync(TokenType.Bot,
                    _discordSettings.Token);

                _client.Ready += ReadyEvent;

                await _client.StartAsync();
                
                var filepath = FileHelper.SetUpFilepath(FilePathType.Root, "moderation", "log");
                
                if (!File.Exists(filepath))
                    await File.WriteAllTextAsync(filepath, $"----------= {DateTimeOffset.UtcNow:F} =----------\n", stoppingToken);
                
                await File.AppendAllTextAsync(filepath, $"----------= {DateTimeOffset.UtcNow:F} =----------\n", stoppingToken);

                if (_config.DiscordActivityName != null)
                {
                    await _client.SetGameAsync(_config.DiscordActivityName, type: (_config.DiscordActivityWatching ? ActivityType.Watching : ActivityType.Playing));
                }

                await InstallCommandsAsync();

                var clientAdapter = new DiscordSocketClientAdapter(_client);

                _configListener.RegisterEvents(_client);
                _userListener.RegisterEvents(_client);
                _messageListener.RegisterEvents(clientAdapter);

                _spamService.RegisterEvents(clientAdapter);
                _raidService.RegisterEvents(clientAdapter);
                _filterService.RegisterEvents(clientAdapter);
                _scheduleService.RegisterEvents(clientAdapter);
                _monitoringService.RegisterEvents(clientAdapter);

                _client.LatencyUpdated += async (int old, int value) =>
                {
                    #if DEBUG
                    _logger.Log(LogLevel.Debug, $"Latency = {value}ms.");
                    #endif

                    if (_config.DiscordActivityName != null)
                    {
                        if (_client.Activity.Name != _config.DiscordActivityName ||
                            _client.Activity.Type != (_config.DiscordActivityWatching ? ActivityType.Watching : ActivityType.Playing)) 
                            await _client.SetGameAsync(_config.DiscordActivityName, type: (_config.DiscordActivityWatching ? ActivityType.Watching : ActivityType.Playing));
                    }
                    else
                    {
                        if (_client.Activity.Name != "") 
                            await _client.SetGameAsync("");
                    }
                };

                _client.Disconnected += async (Exception ex) =>
                {
                    var _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000, stoppingToken);
                        if (_client.ConnectionState is ConnectionState.Disconnected or ConnectionState.Disconnecting)
                        {
                            // Assume softlock, reboot
                            Environment.Exit(254);
                        }

                        if (_client.ConnectionState == ConnectionState.Connecting)
                        {
                            await Task.Delay(5000, stoppingToken);
                            if (_client.ConnectionState is ConnectionState.Disconnected or ConnectionState.Disconnecting or ConnectionState.Connecting)
                            {
                                // Assume softlock, reboot
                                Environment.Exit(254);
                            }
                        }
                    });
                };

                // Block this task until the program is closed.
                await Task.Delay(-1, stoppingToken);
                
                while (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                    await Task.Delay(1000, stoppingToken);
                }
            }
            finally
            {
                await _client.StopAsync();
            }
        }

        private async Task InstallCommandsAsync()
        {
            _client.MessageReceived += async (message) => await DiscordHelper.LeakOrAwaitTask(HandleMessageReceivedAsync(message));

            await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services.BuildServiceProvider());
        }

        // Application command Names have a hard limit of 32 characters,
        // and they seem to get truncated on desktop at ~18-20.
        // There is also a hard limit of 5 context commands per app per guild, so this is it right here.
        private readonly string USERINFO_CMD_NAME = ".userinfo (ephemeral)";
        private readonly string PERMANP_CMD_NAME =  ".permanp";
        private readonly string ADDQUOTE_CMD_NAME = ".addquote";
        private readonly string MOON_CMD_NAME = "moon";
        private readonly string UNMOON_CMD_NAME = "unmoon";
        private readonly string WELCOME_CMD_NAME = "welcome";
        private readonly string UNWELCOME_CMD_NAME = "unwelcome";
        private readonly string TIMEOUT_24H_CMD_NAME = "timeout 24h";

        public async Task ReadyEvent()
        {
            _logger.LogInformation("ReadyEvent() called");

            TaskScheduler.UnobservedTaskException += (object? sender, UnobservedTaskExceptionEventArgs eventArgs) =>
            {
                var unobservedException = eventArgs.Exception.InnerException;
                _logger.LogError($"An UnobservedTaskException occured, i.e. one of Izzy's async tasks threw an exception that remained unhandled " +
                                  "until the task was GC'd. That usually means the issue was in an event handler rather than a command handler.\n" +
                                 $"Unobserved Exception Message: {unobservedException?.Message}\n" +
                                 $"Unobserved Exception Stack: {unobservedException?.StackTrace}");
            };

            foreach (var clientGuild in _client.Guilds)
            {
                _logger.LogDebug($"ReadyEvent() downloading users for guild {clientGuild.Name} ({clientGuild.Id})");
                await clientGuild.DownloadUsersAsync();
            }

            _logger.LogDebug("ReadyEvent() resyncing users");
            ResyncUsers();

            _logger.LogDebug("ReadyEvent() starting unicycle loop");
            _scheduleService.BeginUnicycleLoop(new DiscordSocketClientAdapter(_client));

            _logger.LogDebug("ReadyEvent() setting up application commands");
            _client.MessageCommandExecuted += MessageCommandHandler;
            _client.UserCommandExecuted += UserCommandHandler;

            var guildId = DiscordHelper.DefaultGuild();
            var guild = _client.GetGuild(guildId);

            var userinfoCommand = new UserCommandBuilder()
                .WithName(USERINFO_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.SendMessages);
            var permanpCommand = new UserCommandBuilder()
                .WithName(PERMANP_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.Administrator);
            var addquoteCommand = new MessageCommandBuilder()
                .WithName(ADDQUOTE_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.Administrator);

            var moonCommand = new UserCommandBuilder()
                .WithName(MOON_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.Administrator);
            var unmoonCommand = new UserCommandBuilder()
                .WithName(UNMOON_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.Administrator);
            var welcomeCommand = new UserCommandBuilder()
                .WithName(WELCOME_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.Administrator);
            var unwelcomeCommand = new UserCommandBuilder()
                .WithName(UNWELCOME_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.Administrator);
            var timeoutCommand = new UserCommandBuilder()
                .WithName(TIMEOUT_24H_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.Administrator);

            var moonMessageCommand = new MessageCommandBuilder()
                .WithName(MOON_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.Administrator);
            var welcomeMessageCommand = new MessageCommandBuilder()
                .WithName(WELCOME_CMD_NAME)
                .WithDefaultMemberPermissions(GuildPermission.Administrator);

            try
            {
                await _client.BulkOverwriteGlobalApplicationCommandsAsync(new ApplicationCommandProperties[]
                {
                    userinfoCommand.Build(),
                    permanpCommand.Build(),
                    addquoteCommand.Build(),
                    moonMessageCommand.Build(),
                    welcomeMessageCommand.Build(),
                });
                await guild.BulkOverwriteApplicationCommandAsync(new ApplicationCommandProperties[]
                {
                    moonCommand.Build(),
                    unmoonCommand.Build(),
                    welcomeCommand.Build(),
                    unwelcomeCommand.Build(),
                    timeoutCommand.Build(),
                });
            }
            catch (HttpException exception)
            {
                var json = JsonConvert.SerializeObject(exception.Errors, Formatting.Indented);
                _logger.LogError(json);
            }
        }

        // hardcoding Manechat specific role ids
        private ulong banishedRoleId = 368961099925553153;
        private ulong memberRoleId = 552450130633031700;

        private async Task MessageCommandHandler(SocketMessageCommand command)
        {
            _logger.LogInformation($"MessageCommandHandler received {command.CommandId} {command.CommandName}");

            var guildId = command.GuildId;
            if (guildId == null)
            {
                await command.RespondAsync($"Unable to execute '{command.CommandName}' because the command did not come from any guild/server", ephemeral: true);
                return;
            }

            if (command.CommandName == ADDQUOTE_CMD_NAME)
            {
                var guild = new SocketGuildAdapter(_client.GetGuild((ulong)guildId));

                var output = await QuotesModule.AddQuoteCommandImpl(_quoteService, guild, command.Data.Message.Author.Id, command.Data.Message.Content);

                await command.RespondAsync($"{command.User.Mention} used the '{command.CommandName}' context command:\n\n{output}", allowedMentions: AllowedMentions.None);
            }
            else if (command.CommandName == MOON_CMD_NAME)
            {
                string log = "MessageCommandHandler received invalid user object. Did nothing.";
                if (command.Data.Message.Author is SocketGuildUser)
                {
                    var member = (SocketGuildUser)command.Data.Message.Author;
                    var alreadyHasRole = member.Roles.Select(role => role.Id).Contains(banishedRoleId);
                    if (alreadyHasRole)
                    {
                        log = $"ignored message context command '{MOON_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) because the target user <@{member.Id}> already has the Banished role";
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                    else
                    {
                        log = $"message context command '{MOON_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) on target user <@{member.Id}>";
                        await _modService.AddRole(member, banishedRoleId, log);
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                }
                await command.RespondAsync(log, ephemeral: true);
            }
            else if (command.CommandName == WELCOME_CMD_NAME)
            {
                string log = "MessageCommandHandler received invalid user object. Did nothing.";
                if (command.Data.Message.Author is SocketGuildUser)
                {
                    var member = (SocketGuildUser)command.Data.Message.Author;
                    var alreadyHasRole = member.Roles.Select(role => role.Id).Contains(memberRoleId);
                    if (!alreadyHasRole)
                    {
                        log = $"ignored message context command '{WELCOME_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) because the target user <@{member.Id}> is already lacking the member role";
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                    else
                    {
                        log = $"message context command '{WELCOME_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) on target user <@{member.Id}>";
                        await _modService.RemoveRole(member, memberRoleId, log);
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                }
                await command.RespondAsync(log, ephemeral: true);
            }
            else
            {
                string log = $"MessageCommandHandler received unknown command {command.CommandName}";
                _logger.LogError(log);
                await command.RespondAsync(log, ephemeral: true);
            }
        }

        private async Task UserCommandHandler(SocketUserCommand command)
        {
            _logger.LogInformation($"UserCommandHandler received {command.CommandId} {command.CommandName}");

            var guildId = command.GuildId;
            if (guildId == null)
            {
                await command.RespondAsync($"Unable to execute '{command.CommandName}' because the command did not come from any guild/server", ephemeral: true);
                return;
            }

            if (command.CommandName == USERINFO_CMD_NAME)
            {
                string uinfoOutput = await ModCoreModule.UserInfoImpl(_client, (ulong)guildId, command.Data.Member.Id, _users);

                string log = $"You used the '{command.CommandName}' context command:\n\n{uinfoOutput}";

                await command.RespondAsync(log, allowedMentions: AllowedMentions.None, ephemeral: true);
            }
            else if (command.CommandName == PERMANP_CMD_NAME)
            {
                var permanpOutput = await ModMiscModule.PermaNpCommandIImpl(_scheduleService, _config, command.Data.Member.Id);

                var log = $"{command.User.Mention} used the '{command.CommandName}' context command on {command.Data.Member.Mention}:\n\n{permanpOutput}";

                await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                await command.RespondAsync(log, allowedMentions: AllowedMentions.None, ephemeral: true);
            }
            // There's probably a better way to factor all this, but again, proof of concept
            else if (command.CommandName == MOON_CMD_NAME)
            {
                string log = "UserCommandHandler received invalid user object. Did nothing.";
                if (command.Data.Member is SocketGuildUser)
                {
                    var member = (SocketGuildUser)command.Data.Member;
                    var alreadyHasRole = member.Roles.Select(role => role.Id).Contains(banishedRoleId);
                    if (alreadyHasRole)
                    {
                        log = $"ignored user context command '{MOON_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) because the target user <@{member.Id}> already has the Banished role";
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                    else
                    {
                        log = $"user context command '{MOON_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) on target user <@{member.Id}>";
                        await _modService.AddRole(member, banishedRoleId, log);
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                }
                await command.RespondAsync(log, ephemeral: true);
            }
            else if (command.CommandName == UNMOON_CMD_NAME)
            {
                string log = "UserCommandHandler received invalid user object. Did nothing.";
                if (command.Data.Member is SocketGuildUser)
                {
                    var member = (SocketGuildUser)command.Data.Member;
                    var alreadyHasRole = member.Roles.Select(role => role.Id).Contains(banishedRoleId);
                    if (!alreadyHasRole)
                    {
                        log = $"ignored user context command '{UNMOON_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) because the target user <@{member.Id}> is already lacking the Banished role";
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                    else
                    {
                        log = $"user context command '{UNMOON_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) on target user <@{member.Id}>";
                        await _modService.RemoveRole(member, banishedRoleId, log);
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                }
                await command.RespondAsync(log, ephemeral: true);
            }
            else if (command.CommandName == UNWELCOME_CMD_NAME)
            {
                string log = "UserCommandHandler received invalid user object. Did nothing.";
                if (command.Data.Member is SocketGuildUser)
                {
                    var member = (SocketGuildUser)command.Data.Member;
                    var alreadyHasRole = member.Roles.Select(role => role.Id).Contains(memberRoleId);
                    if (alreadyHasRole)
                    {
                        log = $"ignored user context command '{UNWELCOME_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) because the target user <@{member.Id}> already has the member role";
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                    else
                    {
                        log = $"user context command '{UNWELCOME_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) on target user <@{member.Id}>";
                        await _modService.AddRole(member, memberRoleId, log);
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                }
                await command.RespondAsync(log, ephemeral: true);
            }
            else if (command.CommandName == WELCOME_CMD_NAME)
            {
                string log = "UserCommandHandler received invalid user object. Did nothing.";
                if (command.Data.Member is SocketGuildUser)
                {
                    var member = (SocketGuildUser)command.Data.Member;
                    var alreadyHasRole = member.Roles.Select(role => role.Id).Contains(memberRoleId);
                    if (!alreadyHasRole)
                    {
                        log = $"ignored user context command '{WELCOME_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) because the target user <@{member.Id}> is already lacking the member role";
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                    else
                    {
                        log = $"user context command '{WELCOME_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) on target user <@{member.Id}>";
                        await _modService.RemoveRole(member, memberRoleId, log);
                        await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                    }
                }
                await command.RespondAsync(log, ephemeral: true);
            }
            else if (command.CommandName == TIMEOUT_24H_CMD_NAME)
            {
                string log = "UserCommandHandler received invalid user object. Did nothing.";
                if (command.Data.Member is SocketGuildUser)
                {
                    SocketGuildUser member = (SocketGuildUser)command.Data.Member;

                    await member.SetTimeOutAsync(TimeSpan.FromHours(24));

                    log = $"user context command '{TIMEOUT_24H_CMD_NAME}' used by `{command.User.Username}` ({command.User.Id}) on target user <@{member.Id}>\n" +
                        $"\n" +
                        $"Here's a userlog I unicycled that you can use if you want to!\n```\n" +
                        $"Type: Timeout (24 hours)\n" +
                        $"User: <@{member.Id}> ({member.Username}/{member.Id})\n" +
                        $"Names: {(_users.ContainsKey(member.Id) ? string.Join(", ", _users[member.Id].Aliases) : "None (user isn't known by Izzy)")}\n" +
                        $"```";
                    await _modLog.CreateModLog(_client.GetGuild((ulong)guildId)).SetContent(log).SetFileLogContent(log).Send();
                }
                await command.RespondAsync(log, ephemeral: true);
            }
            else
            {
                string log = $"UserCommandHandler received unknown command {command.CommandName}";
                _logger.LogError(log);
                await command.RespondAsync(log, ephemeral: true);
            }
        }


        private void ResyncUsers()
        {
            Task.Run(async () =>
            {
                var guild = _client.Guilds.Single(guild => guild.Id == _discordSettings.DefaultGuild);

                var result = await UserHelper.scanAllUsers(
                    guild,
                    _users,
                    _config,
                    _modService,
                    _scheduleService,
                    new LoggingService(_logger)
                );

                if (result.newUserCount > 0 || result.roleAddedCounts.Count > 0 || result.newUserRoleUpdatesScheduled.Count > 0)
                {
                    var msg = $"After rebooting I found {result.newUserCount} user(s) who were new to me.";
                    if (result.roleAddedCounts.Count > 0)
                        msg += $"\nAdded {string.Join(", ", result.roleAddedCounts.Select(rac => $"{rac.Value} {guild.GetRole(rac.Key).Name}(s)"))}";
                    if (result.newUserRoleUpdatesScheduled.Count > 0)
                        // TODO: change after ZJR
                        msg += $"\nScheduled `NewMemberRole` removal(s) for {string.Join(", ", result.newUserRoleUpdatesScheduled.Select(u => $"<@{u}>"))}";
                    if (result.roleAddedCounts.Count == 0 && result.newUserRoleUpdatesScheduled.Count == 0)
                        msg += " They required no role changes.";

                    _logger.LogInformation(msg);
                    await _modLog.CreateModLog(guild)
                        .SetContent(msg)
                        .SetFileLogContent(msg)
                        .Send();
                }

                // Get stowaways
                var stowawaySet = new HashSet<SocketGuildUser>();
        
                await foreach (var socketGuildUser in guild.Users.ToAsyncEnumerable())
                {
                    if (socketGuildUser.IsBot) continue; // Bots aren't stowaways
                    if (socketGuildUser.Roles.Select(role => role.Id).Contains(_config.ModRole)) continue; // Mods aren't stowaways

                    if (_config.MemberRole is ulong roleId && !socketGuildUser.Roles.Select(role => role.Id).Contains(roleId))
                    {
                        // Doesn't have member role, add to stowaway list.
                        stowawaySet.Add(socketGuildUser);
                    }
                }

                if (stowawaySet.Count != 0)
                {
                    var stowawayStringList = stowawaySet.Select(user => $"<@{user.Id}>");
                    var stowawayStringFileList = stowawaySet.Select(user => $"{user.DisplayName} ({user.Username}/{user.Id})");
                    
                    await _modLog.CreateModLog(guild)
                        .SetContent($"I found these stowaways after I rebooted, cannot tell if they're new users:\n" +
                                    string.Join(", ", stowawayStringList))
                        .SetFileLogContent($"I found these stowaways after I rebooted, cannot tell if they're new users:\n" +
                                           string.Join(", ", stowawayStringFileList))
                        .Send();
                }
            });
        }

        private async Task HandleMessageReceivedAsync(SocketMessage messageParam)
        {
            if (messageParam.Type != MessageType.Default && messageParam.Type != MessageType.Reply &&
                messageParam.Type != MessageType.ThreadStarterMessage) return;
            if (messageParam is not SocketUserMessage message) return;
            int argPos = 0;
            SocketCommandContext context = new SocketCommandContext(_client, message);

            if (DevSettings.UseDevPrefix)
            {
                _config.Prefix = DevSettings.Prefix;
            }

            if (message.HasCharPrefix(_config.Prefix, ref argPos) ||
                message.Content.StartsWith($"<@{_client.CurrentUser.Id}>"))
            {
                // This kind of non-command happens so often that it's not even worth logging these cases
                if (message.Content.StartsWith($"{_config.Prefix}{_config.Prefix}") ||
                    message.Content.Length == 1)
                    return;

                _logger.Log(LogLevel.Information, $"Received possible command: {messageParam.CleanContent}");

                if (message.Content.StartsWith($"<@{_client.CurrentUser.Id}>"))
                {
                    if (!_config.MentionResponseEnabled)
                    {
                        _logger.Log(LogLevel.Information, $"Ignoring mention because MentionResponseEnabled is false.");
                        return;
                    }
                    if (message.Author.Id == _client.CurrentUser.Id)
                    {
                        _logger.Log(LogLevel.Information, $"Ignoring self-mention.");
                        return;
                    }
                    var secondsSinceLastMention = (DateTimeOffset.UtcNow - _state.LastMentionResponse).TotalSeconds;
                    if (secondsSinceLastMention < _config.MentionResponseCooldown)
                    {
                        _logger.Log(LogLevel.Information, $"Ignoring mention because it's only been {secondsSinceLastMention} seconds since the last one. " +
                            $"(MentionResponseCooldown is {_config.MentionResponseCooldown})");
                        return;
                    }

                    var random = new Random();
                    var index = random.Next(_config.MentionResponses.Count);
                    var response = _config.MentionResponses.ElementAt(index); // Random response

                    _state.LastMentionResponse = DateTimeOffset.UtcNow;

                    await context.Channel.SendMessageAsync($"{response}");
                    return;
                }

                string parsedMessage = message.Content[1..];
                if (char.IsWhiteSpace(parsedMessage[0]))
                {
                    _logger.Log(LogLevel.Information, $"Ignoring message {messageParam.CleanContent} because the {_config.Prefix} is followed by whitespace.");
                    return;
                }


                if (_config.Aliases.Count != 0)
                {
                    var command = parsedMessage.Split(" ");
                        
                    foreach (var keyValuePair in _config.Aliases)
                    {
                        if (command[0].ToLower() != keyValuePair.Key.ToLower()) continue;
                        // Alias match
                            
                        var commandAlias = keyValuePair.Value.StartsWith(_config.Prefix)
                            ? keyValuePair.Value[1..].TrimStart().Split(" ")[0]
                            : keyValuePair.Value.TrimStart().Split(" ")[0];
                            
                        if (_config.Aliases.Any(alias => alias.Key.ToLower() == commandAlias.ToLower()))
                        {
                            await context.Channel.SendMessageAsync(
                                $"**Warning!** This alias directs to another alias!\nIzzy doesn't support aliases feeding into aliases. Please remove this alias or redirect it to an existing command.");
                            return;
                        }

                        if (_commands.Commands.All(cmd => cmd.Name.ToLower() != commandAlias.ToLower()))
                        {
                            await context.Channel.SendMessageAsync(
                                $"**Warning!** This alias directs to a non-existent command!\nPlease remove this alias or redirect it to an existing command.");
                            return;
                        }

                        command[0] = keyValuePair.Value.StartsWith(_config.Prefix)
                            ? keyValuePair.Value[1..]
                            : keyValuePair.Value;
                    }

                    parsedMessage = string.Join(" ", command);
                }
                
                var inputCommandName = parsedMessage.Split(" ")[0];
                var validCommand = _commands.Commands.Any(command => 
                    command.Name.ToLower() == inputCommandName.ToLower() || command.Aliases.Select(alias => alias.ToLower()).Contains(inputCommandName.ToLower()));

                if (!validCommand)
                {
                    var isDev = DiscordHelper.IsDev(context.User.Id);
                    var isMod = (context.User is SocketGuildUser guildUser) && (guildUser.Roles.Any(r => r.Id == _config.ModRole));

                    Func<string, bool> isSuggestable = item =>
                        DiscordHelper.WithinLevenshteinDistanceOf(inputCommandName, item, Convert.ToUInt32(item.Length / 2));

                    Func<CommandInfo, bool> canRunCommand = cinfo =>
                    {
                        var modAttr = cinfo.Preconditions.Any(attribute => attribute is ModCommandAttribute);
                        var devAttr = cinfo.Preconditions.Any(attribute => attribute is DevCommandAttribute);
                        if (modAttr && devAttr) return isMod || isDev;
                        else if (modAttr) return isMod;
                        else if (devAttr) return isDev;
                        else return true;
                    };
                    Func<string, bool> canRunCommandName = name =>
                    {
                        var cinfo = _commands.Commands.Where(c => c.Name == name).SingleOrDefault((CommandInfo?)null);
                        return cinfo is null ? false : canRunCommand(cinfo);
                    };

                    // don't bother searching command.Name because command.Aliases always includes the main name
                    var alternateNamesToSuggest = _commands.Commands.Where(canRunCommand)
                        .SelectMany(c => c.Aliases).Where(isSuggestable);
                    var aliasesToSuggest = _config.Aliases.Where(pair => canRunCommandName(pair.Value.TrimStart().Split(" ")[0]))
                        .Select(pair => pair.Key).Where(isSuggestable);

                    if (alternateNamesToSuggest.Any() || aliasesToSuggest.Any())
                    {
                        var suggestibles = alternateNamesToSuggest.Concat(aliasesToSuggest).Select(s => $"`.{s}`");
                        var suggestionMessage = $"Sorry, I don't have a `.{inputCommandName}` command. Did you mean {string.Join(" or ", suggestibles)}?";
                        await context.Channel.SendMessageAsync(suggestionMessage);
                        return;
                    }
                    else
                    {
                        _logger.Log(LogLevel.Information, $"Ignoring message {messageParam.CleanContent} because it doesn't match " +
                            $"any command or alias names, and is not similar enough to any of them to make a suggestion.");
                        return;
                    }
                }
                
                var searchResult = _commands.Search(parsedMessage);
                var commandToExec = searchResult.Commands[0].Command;

                // Check for DMsAllowed attribute
                var hasExternalUsageAllowedAttribute = commandToExec.Preconditions.Where(attribute => attribute != null).OfType<ExternalUsageAllowed>().Any();

                if (!DiscordHelper.ShouldExecuteInPrivate(hasExternalUsageAllowedAttribute, context)) return;

                // Check for BotsAllowed attribute
                var hasBotsAllowedAttribute = commandToExec.Preconditions.Where(attribute => attribute != null).OfType<BotsAllowedAttribute>().Any();
                if (!hasBotsAllowedAttribute && context.User.IsBot)
                {
                    _logger.LogInformation($"Ignoring command '{messageParam.CleanContent}' because it comes from a bot and {_config.Prefix}{commandToExec.Name} lacks a [BotsAllowed] attribute.");
                    return;
                }

                var result = await _commands.ExecuteAsync(context, parsedMessage, _services.BuildServiceProvider());
                if (result.Error == CommandError.ParseFailed &&
                    result.ErrorReason.StartsWith("Failed to parse "))
                {
                    await context.Channel.SendMessageAsync(
                        $"Sorry, I was unable to process that command because when I tried to parse a value into an {result.ErrorReason.Split(" ")[3]} but failed." +
                        $"Please run `.help {commandToExec.Name}` for usage information about this command.");
                }

                if (result.Error == CommandError.Exception)
                {
                    await context.Channel.SendMessageAsync(
                        $"Sorry, something went wrong while processing that command.");

                    var underlyingException = ((Discord.Commands.ExecuteResult)result).Exception;
                    _logger.LogError($"An exception occured while processing a command:\n" +
                                     $"Command: {parsedMessage}\n" +
                                     $"Exception Message: {underlyingException.Message}\n" +
                                     $"Exception Stack: {underlyingException.StackTrace}");
                }
            }
        }

        private Task Log(LogMessage msg)
        {
            if (msg.Exception != null)
            {
                if (msg.Exception.Message == "Server missed last heartbeat")
                {
                    _logger.LogWarning("Izzy Moonbot missed a heartbeat (likely network interruption).");
                }
                else
                {
                    _logger.LogError("Izzy Moonbot has encountered an error. Logging information...");
                    _logger.LogError($"Message: {msg.Exception.Message}");
                    _logger.LogError($"Source: {msg.Exception.Source}");
                    _logger.LogError($"HResult: {msg.Exception.HResult}");
                    _logger.LogError($"Stack trace: {msg.Exception.StackTrace}");
                }
            }

            _logger.LogInformation(msg.Message);
            return Task.CompletedTask;
        }
    }
}
