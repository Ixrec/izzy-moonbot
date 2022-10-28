using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using Izzy_Moonbot.Attributes;
using Izzy_Moonbot.EventListeners;
using Izzy_Moonbot.Helpers;
using Izzy_Moonbot.Service;
using Izzy_Moonbot.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        private readonly Dictionary<ulong, User> _users;
        private readonly UserListener _userListener;
        private DiscordSocketClient _client;
        public bool hasProgrammingSocks = true;
        public int LaserCount = 10;

        public Worker(ILogger<Worker> logger, ModLoggingService modLog, IServiceCollection services, ModService modService, RaidService raidService,
            FilterService filterService, ScheduleService scheduleService, IOptions<DiscordSettings> discordSettings,
            Config config, Dictionary<ulong, User> users, UserListener userListener, SpamService spamService)
        {
            _logger = logger;
            _modLog = modLog;
            _modService = modService;
            _raidService = raidService;
            _filterService = filterService;
            _scheduleService = scheduleService;
            _commands = new CommandService();
            _discordSettings = discordSettings.Value;
            _services = services;
            _config = config;
            _users = users;
            _userListener = userListener;
            _spamService = spamService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var discordConfig = new DiscordSocketConfig { GatewayIntents = GatewayIntents.All, MessageCacheSize = 50 };
                _client = new DiscordSocketClient(discordConfig);
                _client.Log += Log;
                await _client.LoginAsync(TokenType.Bot,
                    _discordSettings.Token);

                _client.Ready += ReadyEvent;

                await _client.StartAsync();

                if (_config.DiscordActivityName != null)
                {
                    await _client.SetGameAsync(_config.DiscordActivityName, type: (_config.DiscordActivityWatching ? ActivityType.Watching : ActivityType.Playing));
                }

                await InstallCommandsAsync();
                
                _userListener.RegisterEvents(_client);
                
                _spamService.RegisterEvents(_client);
                _raidService.RegisterEvents(_client);
                _filterService.RegisterEvents(_client);

                _client.LatencyUpdated += async (int old, int value) =>
                {
                    _logger.Log(LogLevel.Debug, $"Latency = {value}ms.");
                    
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
                    Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        if (_client.ConnectionState == ConnectionState.Disconnected)
                        {
                            // Assume softlock, reboot
                            Environment.Exit(254);
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
            _client.MessageReceived += HandleCommandAsync;
            await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services.BuildServiceProvider());
        }

        public async Task ReadyEvent()
        {
            _logger.LogTrace("Ready event called");
            _scheduleService.ResumeScheduledTasks(_client.Guilds.ToArray()[0]);
            
            foreach (var clientGuild in _client.Guilds)
            {
                await clientGuild.DownloadUsersAsync();
            }
        }

        private async Task HandleCommandAsync(SocketMessage messageParam)
        {
            //_logger.Log(LogLevel.Debug, $"{messageParam.CleanContent}; {messageParam.EditedTimestamp}");
            if (messageParam.Type != MessageType.Default && messageParam.Type != MessageType.Reply &&
                messageParam.Type != MessageType.ThreadStarterMessage) return;
            SocketUserMessage message = messageParam as SocketUserMessage;
            int argPos = 0;
            SocketCommandContext context = new SocketCommandContext(_client, message);

            if (DevSettings.UseDevPrefix)
            {
                _config.Prefix = DevSettings.Prefix;
            }

            if (message.HasCharPrefix(_config.Prefix, ref argPos) ||
                message.Content.StartsWith($"<@{_client.CurrentUser.Id}>"))
            {
                string parsedMessage = null;
                bool checkCommands = true;
                if (message.Content.StartsWith($"<@{_client.CurrentUser.Id}>"))
                {
                    checkCommands = false;
                    parsedMessage = "<mention>";
                }
                else
                {
                    parsedMessage = DiscordHelper.CheckAliasesAsync(message.Content, _config);
                }

                if (checkCommands)
                {
                    bool validCommand = false;
                    foreach (var command in _commands.Commands)
                    {
                        if (command.Name != parsedMessage.Split(" ")[0])
                        {
                            continue;
                        }

                        validCommand = true;
                        break;
                    }

                    if (!validCommand) return;
                }

                // Check for BotsAllowed attribute
                bool hasBotsAllowedAttribute = false;
                SearchResult searchResult = _commands.Search(parsedMessage);
                CommandInfo commandToExec = searchResult.Commands[0].Command;

                foreach (var attribute in commandToExec.Attributes)
                {
                    if (attribute == null) continue;
                    if (!(attribute is BotsAllowedAttribute)) continue;

                    hasBotsAllowedAttribute = true;
                    break;
                }

                if (!hasBotsAllowedAttribute && context.User.IsBot) return;

                await _commands.ExecuteAsync(context, parsedMessage, _services.BuildServiceProvider());
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