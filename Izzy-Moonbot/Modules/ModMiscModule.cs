using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Flurl.Http;
using Izzy_Moonbot.Adapters;
using Izzy_Moonbot.Attributes;
using Izzy_Moonbot.Helpers;
using Izzy_Moonbot.Service;
using Izzy_Moonbot.Settings;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using static Izzy_Moonbot.EventListeners.ConfigListener;

namespace Izzy_Moonbot.Modules;

[Summary("Moderator-only commands that are either infrequently used or just for fun.")]
public class ModMiscModule : ModuleBase<SocketCommandContext>
{
    private readonly Config _config;
    private readonly ScheduleService _schedule;
    private readonly Dictionary<ulong, User> _users;
    private readonly LoggingService _logger;
    private readonly TransientState _state;
    private readonly ModService _mod;

    public ModMiscModule(Config config, Dictionary<ulong, User> users, ScheduleService schedule, LoggingService logger, TransientState state, ModService modService)
    {
        _config = config;
        _schedule = schedule;
        _users = users;
        _logger = logger;
        _state = state;
        _mod = modService;
    }

    [Command("panic")]
    [Summary("Immediately disconnects the client in case of emergency.")]
    [Remarks("This should only be used if Izzy starts doing something terrible to Manechat and we can't afford to wait for proper debugging.")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    public async Task PanicCommand()
    {
        // Just closes the connection.
        await ReplyAsync("<a:izzywhat:891381404741550130>");
        Environment.Exit(255);
    }

    [Command("permanp")]
    [Summary("Remove the scheduled new pony role removal for this user, essentially meaning they keep the new pony role until manually removed.")]
    [Remarks("In the Discord UI, right-click on a user's name and go to 'Apps' for an alternative way of invoking this command.")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("user", ParameterType.UnambiguousUser, "The user to remove the scheduled removal from.")]
    public async Task PermaNpCommandAsync(
        [Remainder]string argsString = "")
    {
        if (argsString == "")
        {
            await ReplyAsync(
                "Hey uhh... I can't remove the scheduled new pony role removal for a user if you haven't given me the user to remove it from...");
            return;
        }

        if (ParseHelper.TryParseUnambiguousUser(argsString, out var userErrorString) is not var (userId, _))
        {
            await Context.Channel.SendMessageAsync($"Failed to get a user id from the first argument: {userErrorString}");
            return;
        }

        var output = await PermaNpCommandIImpl(_schedule, _config, userId);

        await ReplyAsync(output);
    }

    static public async Task<string> PermaNpCommandIImpl(ScheduleService scheduleService, Config config, ulong userId)
    {
        var getSingleNewPonyRemoval = new Func<ScheduledJob, bool>(job =>
            job.Action is ScheduledRoleRemovalJob removalJob &&
            removalJob.User == userId &&
            removalJob.Role == config.NewMemberRole);

        var job = scheduleService.GetScheduledJob(getSingleNewPonyRemoval);
        if (job != null)
        {
            await scheduleService.DeleteScheduledJob(job);

            return $"Removed the scheduled new pony role removal from <@{userId}>.";
        }
        else
        {
            return $"I couldn't find a scheduled new pony role removal for <@{userId}>. It either already occured or they already have permanent new pony.";
        }
    }

    [Command("scan")]
    [Summary("Refresh all the user information tracked by Izzy")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    public async Task ScanCommandAsync()
    {
        var result = await UserHelper.scanAllUsers(
            Context.Guild,
            _users,
            _config,
            _mod,
            _schedule,
            _logger
        );

        var reply = $"Done! I found {result.totalUsersCount} users in this server. " +
            $"{result.updatedUserCount} required a userinfo update, of which {result.newUserCount} were new to me. " +
            $"The other {result.totalUsersCount - result.updatedUserCount} were up to date.";
        if (result.roleAddedCounts.Count > 0)
            reply += $"\nAdded {string.Join(", ", result.roleAddedCounts.Select(rac => $"{rac.Value} <@&{rac.Key}>(s)"))}";
        if (result.newUserRoleUpdatesScheduled.Count > 0)
            // TODO: change after ZJR
            reply += $"\nScheduled <@&{_config.NewMemberRole!.Value}> removal(s) for {string.Join(", ", result.newUserRoleUpdatesScheduled.Select(u => $"<@{u}>"))}";

        _logger.Log(reply);
        await Context.Message.ReplyAsync(reply, allowedMentions: AllowedMentions.None);
    }

    [Command("echo")]
    [Summary("Posts a message (and/or sticker) to a specified channel")]
    [Remarks("See .remind for sending a message in the future, or .remindme for sending a direct message to yourself in the future.")]
    [ModCommand(Group = "Permission")]
    [DevCommand(Group = "Permission")]
    [Parameter("channel", ParameterType.Channel, "The channel to send the message to.", true)]
    [Parameter("content", ParameterType.String, "The message to send.")]
    [BotsAllowed]
    public async Task EchoCommandAsync(
        [Remainder] string argsString = "")
    {
        await TestableEchoCommandAsync(
            new SocketCommandContextAdapter(Context),
            argsString,
            // retrieving the stickers requires knowing that Context.Message is a SocketUserMessage instead of any other
            // IUserMessage, which is not worth the hassle of trying to simulate in tests when we're only going to echo them
            Context.Message.Stickers.Where(s => s.IsAvailable ?? false).ToArray()
        );
    }

    public async Task TestableEchoCommandAsync(
        IIzzyContext context,
        string argsString = "",
        ISticker[]? stickers = null)
    {
        ulong? channelId;
        string message;
        if (ParseHelper.TryParseChannelResolvable(argsString, context, out var channelParseError) is var (parsedChannelId, argsAfterChannel))
        {
            message = DiscordHelper.StripQuotes(argsAfterChannel);
            channelId = parsedChannelId;
        }
        else
        {
            message = DiscordHelper.StripQuotes(argsString);
            channelId = null;
        }

        if (message == "" && (stickers is null || !stickers.Any()))
        {
            await context.Channel.SendMessageAsync("You must provide either a non-empty message or an available sticker for me to echo.");
            return;
        }

        if (channelId != null)
        {
            var channel = context.Guild?.GetTextChannel((ulong)channelId);
            if (channel != null)
            {
                await channel.SendMessageAsync(message, stickers: stickers);
                return;
            }

            await context.Channel.SendMessageAsync("I can't send a message there.");
            return;
        }

        await context.Channel.SendMessageAsync(message, stickers: stickers);
    }

    [Command("stowaways")]
    [Summary("List non-bot, non-mod users who do not have the member role.")]
    [Remarks("These are most likely users that Izzy or a human moderator silenced or banished, but no one ever got around to kicking, banning, unsilencing or unbanishing them.")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    public async Task StowawaysCommandAsync()
    {
        await Task.Run(async () =>
        {
            if (!Context.Guild.HasAllMembers) await Context.Guild.DownloadUsersAsync();

            var stowawaySet = new HashSet<SocketGuildUser>();
            
            await foreach (var socketGuildUser in Context.Guild.Users.ToAsyncEnumerable())
            {
                if (socketGuildUser.IsBot) continue; // Bots aren't stowaways
                if (socketGuildUser.Roles.Select(role => role.Id).Contains(_config.ModRole)) continue; // Mods aren't stowaways

                if (socketGuildUser.Roles.Select(role => role.Id).Contains(DiscordHelper.BanishedRoleId))
                {
                    // Doesn't have member role, add to stowaway set.
                    stowawaySet.Add(socketGuildUser);
                }
            }

            if (stowawaySet.Count == 0)
            {
                // There's no stowaways
                await ReplyAsync("I didn't find any stowaways.");
            }
            else
            {
                var stowawayStringList = stowawaySet.Select(user => $"<@{user.Id}>");

                await ReplyAsync(
                    $"I found these following stowaways:\n{string.Join(", ", stowawayStringList)}");
            }
        });
    }

    [Command("schedule")]
    [Summary("View and modify Izzy's scheduler.")]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("[...]", ParameterType.Complex, "")]
    public async Task ScheduleCommandAsync([Remainder]string argsString = "")
    {
        await TestableScheduleCommandAsync(
            new SocketCommandContextAdapter(Context),
            argsString
        );
    }

    public async Task TestableScheduleCommandAsync(
        IIzzyContext context,
        string argsString = "")
    {
        var jobTypes = new Dictionary<string, Type>
        {
            { "remove-role", typeof(ScheduledRoleRemovalJob) },
            { "add-role", typeof(ScheduledRoleAdditionJob) },
            { "unban", typeof(ScheduledUnbanJob) },
            { "echo", typeof(ScheduledEchoJob) },
            { "banner", typeof(ScheduledBannerRotationJob) },
            { "bored", typeof(ScheduledBoredCommandsJob) },
            { "endraid", typeof(ScheduledEndRaidJob) },
        };
        var supportedJobTypesMessage = $"The currently supported job types are: {string.Join(", ", jobTypes.Keys.Select(k => $"`{k}`"))}";

        if (argsString == "")
        {
            await context.Channel.SendMessageAsync(
                $"Heya! Here's a list of subcommands for {_config.Prefix}schedule!\n" +
                $"\n" +
                $"`{_config.Prefix}schedule list [jobtype]` - Show all scheduled jobs (or all jobs of the specified type) in a Discord message.\n" +
                $"`{_config.Prefix}schedule list-file [jobtype]` - Post a text file attachment listing all scheduled jobs (or all jobs of the specified type).\n" +
                $"`{_config.Prefix}schedule about <jobtype>` - Get information about a job type, including the `.schedule add` syntax to create one.\n" +
                $"`{_config.Prefix}schedule about <id>` - Get information about a specific scheduled job by its ID.\n" +
                $"`{_config.Prefix}schedule add <jobtype> <date/time> [...]` - Create and schedule a job. Run `{_config.Prefix}schedule about <jobtype>` to figure out the arguments.\n" +
                $"`{_config.Prefix}schedule remove <id>` - Remove a scheduled job by its ID.\n" +
                $"\n" +
                $"{supportedJobTypesMessage}\n" +
                $"All of Izzy's <date/time> formats are supported (see `.help remindme`).");
            return;
        }

        var args = DiscordHelper.GetArguments(argsString);
        
        if (args.Arguments[0].ToLower() == "list")
        {
            if (args.Arguments.Length == 1)
            {
                // All
                var jobs = _schedule.GetScheduledJobs()
                    .OrderBy(job => job.ExecuteAt)
                    .Select(job => job.ToDiscordString()).ToList();

                PaginationHelper.PaginateIfNeededAndSendMessage(
                    context,
                    "Heya! Here's a list of all the scheduled jobs sorted by next execution time!\n",
                    jobs,
                    "\nIf you need a raw text file, run `.schedule list-file`.",
                    pageSize: 5,
                    codeblock: false,
                    allowedMentions: AllowedMentions.None
                );
            }
            else
            {
                // Specific job type
                var jobType = string.Join("", argsString.Skip(args.Indices[0]));

                if (jobTypes[jobType] is not Type type)
                {
                    await context.Channel.SendMessageAsync(
                        $"There is no \"{jobType}\" job type.\n{supportedJobTypesMessage}");
                    return;
                }

                var jobs = _schedule.GetScheduledJobs()
                    .Where(job => job.Action.GetType().FullName == type.FullName)
                    .OrderBy(job => job.ExecuteAt)
                    .Select(job => job.ToDiscordString()).ToList();

                PaginationHelper.PaginateIfNeededAndSendMessage(
                    context,
                    $"Heya! Here's a list of all the scheduled {jobType} jobs sorted by next execution time!\n",
                    jobs,
                    $"\nIf you need a raw text file, run `.schedule list-file {jobType}`.",
                    pageSize: 5,
                    codeblock: false,
                    allowedMentions: AllowedMentions.None
                );
            }
        }
        else if (args.Arguments[0].ToLower() == "list-file")
        {
            if (args.Arguments.Length == 1)
            {
                // All
                var jobs = _schedule.GetScheduledJobs().Select(job => job.ToFileString()).ToList();
                
                var s = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', jobs)));
                var fa = new FileAttachment(s, $"all_scheduled_jobs_{DateTimeHelper.UtcNow.ToUnixTimeSeconds()}.txt");

                await context.Channel.SendFileAsync(fa, $"Here's the file list of all scheduled jobs!");
            }
            else
            {
                // Specific job type
                var jobType = string.Join("", argsString.Skip(args.Indices[0]));

                if (jobTypes[jobType] is not Type type)
                {
                    await context.Channel.SendMessageAsync(
                        $"There is no \"{jobType}\" job type.\n{supportedJobTypesMessage}");
                    return;
                }
                
                var jobs = _schedule.GetScheduledJobs().Where(job => job.Action.GetType().FullName == type.FullName).Select(job => job.ToFileString()).ToList();
                
                var s = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', jobs)));
                var fa = new FileAttachment(s, $"{jobType}_scheduled_jobs_{DateTimeHelper.UtcNow.ToUnixTimeSeconds()}.txt");

                await context.Channel.SendFileAsync(fa, $"Here's the file list of all scheduled {jobType} jobs!");
            }
        }
        else if (args.Arguments[0].ToLower() == "about")
        {
            var searchString = string.Join("", argsString.Skip(args.Indices[0]));

            if (searchString == "")
            {
                await context.Channel.SendMessageAsync("You need to provide either a job type, or an ID for a specific job.");
                return;
            }
            
            // Check IDs first
            var potentialJob = _schedule.GetScheduledJob(searchString);
            if (potentialJob != null)
            {
                // Not null, this job exists. Display information about it.
                var jobType = jobTypes.First(kv => kv.Value == potentialJob.Action.GetType()).Key;

                var expandedJobInfo = potentialJob.Action switch
                {
                    ScheduledRoleJob roleJob => $"Target user: <@{roleJob.User}>\n" +
                                                $"Target role: <@&{roleJob.Role}>\n" +
                                                $"{(roleJob.Reason != null ? $"Reason: {roleJob.Reason}\n" : "")}",
                    ScheduledUnbanJob unbanJob => $"Target user: <@{unbanJob.User}>\n",
                    ScheduledEchoJob echoJob => $"Target channel/user: <#{echoJob.ChannelOrUser}> / <@{echoJob.ChannelOrUser}>\n" +
                                                $"Content:\n```\n{echoJob.Content}\n```\n",
                    ScheduledBannerRotationJob => $"Current banner mode: {_config.BannerMode}\n" +
                                                  $"Configure this job via `.config`.\n",
                    ScheduledEndRaidJob endRaidJob => $"Raid IsLarge: {endRaidJob.IsLarge}",
                    _ => ""
                };
                
                var expandedRepeatInfo = potentialJob.RepeatType switch
                {
                    ScheduledJobRepeatType.None => "",
                    ScheduledJobRepeatType.Relative => ConstructRelativeRepeatTimeString(potentialJob) + "\n",
                    ScheduledJobRepeatType.Daily => $"Every day at {potentialJob.ExecuteAt:T} UTC\n",
                    ScheduledJobRepeatType.Weekly => $"Every week at {potentialJob.ExecuteAt:T} on {potentialJob.ExecuteAt:dddd}\n",
                    ScheduledJobRepeatType.Yearly => $"Every year at {potentialJob.ExecuteAt:T} on {potentialJob.ExecuteAt:dd MMMM}\n",
                    _ => throw new NotImplementedException("Unknown repeat type.")
                };
                
                await context.Channel.SendMessageAsync(
                    $"Here's information regarding the scheduled job with ID of `{potentialJob.Id}`:\n" +
                    $"Job type: {jobType}\n" +
                    $"Created <t:{potentialJob.CreatedAt.ToUnixTimeSeconds()}:F>\n" +
                    $"Executes <t:{potentialJob.ExecuteAt.ToUnixTimeSeconds()}:R>\n" +
                    $"{expandedRepeatInfo}" +
                    $"{expandedJobInfo}", allowedMentions: AllowedMentions.None);
            }
            else
            {
                // Not an id, so must be a job type
                if (jobTypes[searchString] is not Type type)
                {
                    await context.Channel.SendMessageAsync(
                        $"There is no \"{searchString}\" job ID or job type.\n{supportedJobTypesMessage}");
                    return;
                }
                
                var content = "";
                switch (type.Name)
                {
                    case "ScheduledRoleRemovalJob":
                        content = $"""
                            **Role Removal**
                            *Removes a role from a user after a specified amount of time.*
                            Creation syntax:
                            ```
                            {_config.Prefix}schedule add {searchString} <date/time> <role id> <user id> [reason]
                            ```
                            `user id` - The id of the user to remove the role from.
                            `role id` - The id of the role to remove.
                            `reason` - Optional reason (if omitted, one will be autogenerated).
                            """;
                        break;
                    case "ScheduledRoleAdditionJob":
                        content = $"""
                            **Role Addition**
                            *Adds a role to a user in a specified amount of time.*
                            Creation syntax:
                            ```
                            {_config.Prefix}schedule add {searchString} <date/time> <role id> <user id> [reason]
                            ```
                            `user id` - The id of the user to add the role to.
                            `role id` - The id of the role to add.
                            `reason` - Optional reason (if omitted, one will be autogenerated).
                            """;
                        break;
                    case "ScheduledUnbanJob":
                        content = $"""
                            **Unban User**
                            *Unbans a user after a specified amount of time.*
                            Creation syntax:
                            ```
                            {_config.Prefix}schedule add {searchString} <date/time> <user id>
                            ```
                            `user id` - The id of the user to unban.
                            """;
                        break;
                    case "ScheduledEchoJob":
                        content = $"""
                            **Echo**
                            *Sends a message in a channel, or to a users DMs.*
                            Creation syntax:
                            ```
                            {_config.Prefix}schedule add {searchString} <date/time> <channel/user id> <content>
                            ```
                            `channel/user id` - The id of either the channel or user to send the message to.
                            `content` - The message to send.
                            """;
                        break;
                    case "ScheduledBannerRotationJob":
                        content = $"""
                            **Banner Rotation**
                            *Runs banner rotation, or checks Manebooru for featured image depending on `BannerMode`.*
                            :warning: This scheduled job is managed by Izzy internally. It is best not to modify it with this command.
                            Creation syntax:
                            ```
                            {_config.Prefix}schedule add {searchString} <date/time>
                            ```
                            """;
                        break;
                    case "ScheduledBoredCommandsJob":
                        content = $"""
                            **Bored Commands**
                            *If no one has posted in `BoredChannel` within the last `BoredCooldown` seconds, this job posts one of the strings in `BoredCommands`, typically a command meant to be executed by Izzy herself or another bot. Then reschedules itself for `BoredCooldown` seconds in the future.*
                            :warning: This scheduled job is managed by Izzy internally. It is best not to modify it with this command.
                            Creation syntax:
                            ```
                            {_config.Prefix}schedule add {searchString} <date/time>
                            ```
                            """;
                        break;
                    case "ScheduledEndRaidJob":
                        content = $"""
                            **End Raid**
                            Resets Izzy's internal raid-related state some time after a possible raid is detected. See `.help ass` for more context.
                            :warning: This scheduled job is managed by Izzy internally. It is best not to modify it with this command.
                            Creation syntax:
                            ```
                            {_config.Prefix}schedule add {searchString} <date/time> <islarge>
                            ```
                            """;
                        break;
                    default:
                        content = $"""
                            **Unknown type**
                            *I don't know what this type is?*
                            """;
                        break;
                }

                await context.Channel.SendMessageAsync(content, allowedMentions: AllowedMentions.None);
            }
        } 
        else if (args.Arguments[0].ToLower() == "add")
        {
            if (args.Arguments.Length == 1)
            {
                await context.Channel.SendMessageAsync("What did you want me to add?");
                return;
            }

            var typeArg = args.Arguments[1];
            if (jobTypes[typeArg] is not Type type)
            {
                await context.Channel.SendMessageAsync($"There is no \"{typeArg}\" job ID or job type.\n{supportedJobTypesMessage}");
                return;
            }

            var timeArgString = string.Join("", argsString.Skip(args.Indices[1]));
            if (ParseHelper.TryParseDateTime(timeArgString, out var parseError) is not var (parseResult, actionArgsString))
            {
                await context.Channel.SendMessageAsync($"Failed to comprehend time: {parseError}");
                return;
            }

            var actionArgs = DiscordHelper.GetArguments(actionArgsString);
            var actionArgTokens = actionArgs.Arguments;
            ScheduledJobAction action;
            switch (typeArg)
            {
                case "remove-role":
                    {
                        if (!ulong.TryParse(actionArgTokens.ElementAt(0), out ulong roleId))
                        {
                            await context.Channel.SendMessageAsync($"Failed to parse arguments: \"{actionArgTokens.ElementAt(0)}\" is not a role id");
                            return;
                        }
                        if (!ulong.TryParse(actionArgTokens.ElementAt(1), out ulong userId))
                        {
                            await context.Channel.SendMessageAsync($"Failed to parse arguments: \"{actionArgTokens.ElementAt(1)}\" is not a user id");
                            return;
                        }
                        var reason = actionArgTokens.Length >= 2 ? actionArgTokens.ElementAtOrDefault(2) : await DiscordHelper.AuditLogForCommand(context);
                        action = new ScheduledRoleRemovalJob(roleId, userId, reason);
                        break;
                    }
                case "add-role":
                    {
                        if (!ulong.TryParse(actionArgTokens.ElementAt(0), out ulong roleId))
                        {
                            await context.Channel.SendMessageAsync($"Failed to parse arguments: \"{actionArgTokens.ElementAt(0)}\" is not a role id");
                            return;
                        }
                        if (!ulong.TryParse(actionArgTokens.ElementAt(1), out ulong userId))
                        {
                            await context.Channel.SendMessageAsync($"Failed to parse arguments: \"{actionArgTokens.ElementAt(1)}\" is not a user id");
                            return;
                        }
                        var reason = actionArgTokens.Length >= 2 ? actionArgTokens.ElementAtOrDefault(2) : await DiscordHelper.AuditLogForCommand(context);
                        action = new ScheduledRoleAdditionJob(roleId, userId, reason);
                        break;
                    }
                case "unban":
                    {
                        if (!ulong.TryParse(actionArgTokens.ElementAt(0), out ulong userId))
                        {
                            await context.Channel.SendMessageAsync($"Failed to parse arguments: \"{actionArgTokens.ElementAt(0)}\" is not a user id");
                            return;
                        }
                        action = new ScheduledUnbanJob(userId, await DiscordHelper.AuditLogForCommand(context));
                        break;
                    }
                case "echo":
                    if (!ulong.TryParse(actionArgTokens.ElementAt(0), out ulong channelId))
                    {
                        await context.Channel.SendMessageAsync($"Failed to parse arguments: \"{actionArgTokens.ElementAt(0)}\" is not a channel/user id");
                        return;
                    }
                    action = new ScheduledEchoJob(channelId, string.Join("", actionArgsString.Skip(actionArgs.Indices[0])));
                    break;
                case "banner":
                    action = new ScheduledBannerRotationJob();
                    break;
                case "bored":
                    action = new ScheduledBoredCommandsJob();
                    break;
                case "endraid":
                    if (!bool.TryParse(actionArgTokens.ElementAt(0), out bool isLarge))
                    {
                        await context.Channel.SendMessageAsync($"Failed to parse arguments: \"{actionArgTokens.ElementAt(0)}\" is not a boolean");
                        return;
                    }
                    action = new ScheduledEndRaidJob(isLarge);
                    break;
                default: throw new InvalidCastException($"{typeArg} is not a valid job type");
            };

            var job = new ScheduledJob(DateTimeHelper.UtcNow, parseResult.Time, action, parseResult.RepeatType);
            await _schedule.CreateScheduledJob(job);
            await context.Channel.SendMessageAsync($"Created scheduled job: {job.ToDiscordString()}", allowedMentions: AllowedMentions.None);
        }
        else if (args.Arguments[0].ToLower() == "remove")
        {
            var searchString = string.Join("", argsString.Skip(args.Indices[0]));

            if (searchString == "")
            {
                await context.Channel.SendMessageAsync("You need to provide an ID for a specific scheduled job.");
                return;
            }
            
            // Check IDs first
            var potentialJob = _schedule.GetScheduledJob(searchString);
            if (potentialJob == null)
            {
                await context.Channel.SendMessageAsync("Sorry, I couldn't find that job.");
                return;
            }

            try
            {
                await _schedule.DeleteScheduledJob(potentialJob);

                await context.Channel.SendMessageAsync("Successfully deleted scheduled job.");
            }
            catch (NullReferenceException)
            {
                await context.Channel.SendMessageAsync("Sorry, I couldn't find that job.");
            }
        }
    } 
    
    private static string ConstructRelativeRepeatTimeString(ScheduledJob job)
    {
        var secondsBetweenExecution = job.ExecuteAt.ToUnixTimeSeconds() - (job.LastExecutedAt?.ToUnixTimeSeconds() ?? job.CreatedAt.ToUnixTimeSeconds());

        var seconds = Math.Floor(double.Parse(secondsBetweenExecution.ToString()) % 60);
        var minutes = Math.Floor(double.Parse(secondsBetweenExecution.ToString()) / 60 % 60);
        var hours = Math.Floor(double.Parse(secondsBetweenExecution.ToString()) / 60 / 60 % 24);
        var days = Math.Floor(double.Parse(secondsBetweenExecution.ToString()) / 60 / 60 / 24);

        return $"Executes every {(days == 0 ? "" : $"{days} Day{(days is < 1.9 and > 0.9 ? "" : "s")}")} " +
               $"{(hours == 0 ? "" : $"{hours} Hour{(hours is < 1.9 and > 0.9 ? "" : "s")}")} " +
               $"{(minutes == 0 ? "" : $"{minutes} Minute{(minutes is < 1.9 and > 0.9 ? "" : "s")}")} " +
               $"{(seconds == 0 ? "" : $"{seconds} Second{(seconds is < 1.9 and > 0.9 ? "" : "s")}")}";
    }

    [Command("remind")]
    [Summary("Ask Izzy to send a message to a channel in the future.")]
    [Remarks("See .echo for sending a message immediately, or .remindme for sending a direct message to yourself.")]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("channel", ParameterType.Channel, "The channel to send the message to.")]
    [Parameter("time", ParameterType.DateTime, "When to send the message, whether it repeats, etc. See `.help remindme` for supported formats.")]
    [Parameter("message", ParameterType.String, "The reminder message to send.")]
    [Example(".remind #manechat in 2 hours join stream")]
    [Example(".remind #tailchat at 4:30pm UTC-7 go shopping")]
    [Example(".remind #modchat on 1 jan 2020 12:00 UTC+0 rethink life")]
    public async Task RemindCommandAsync([Remainder] string argsString = "")
    {
        await TestableRemindCommandAsync(
            new SocketCommandContextAdapter(Context),
            argsString
        );
    }

    public async Task TestableRemindCommandAsync(
        IIzzyContext context,
        string argsString = "")
    {
        if (argsString == "")
        {
            await context.Channel.SendMessageAsync($"Remind you of what now? (see `.help remind`)");
            return;
        }

        if (ParseHelper.TryParseChannelResolvable(argsString, context, out var channelParseError) is not var (channelId, argsAfterChannel))
        {
            await ReplyAsync($"Failed to get a channel: {channelParseError}");
            return;
        }

        if (ParseHelper.TryParseDateTime(argsAfterChannel, out var parseError) is not var (parseResult, content))
        {
            await context.Channel.SendMessageAsync($"Failed to comprehend time: {parseError}");
            return;
        }

        if (content == "")
        {
            await context.Channel.SendMessageAsync("You have to tell me what to send!");
            return;
        }

        _logger.Log($"Adding scheduled job to post \"{content}\" in channel {channelId} at {parseResult.Time:F}{(parseResult.RepeatType == ScheduledJobRepeatType.None ? "" : $" repeating {parseResult.RepeatType}")}",
            context: context, level: LogLevel.Debug);
        var action = new ScheduledEchoJob(channelId, content);
        var task = new ScheduledJob(DateTimeHelper.UtcNow, parseResult.Time, action, parseResult.RepeatType);
        await _schedule.CreateScheduledJob(task);

        await context.Channel.SendMessageAsync($"Okay! I'll send that reminder to <#{channelId}> <t:{parseResult.Time.ToUnixTimeSeconds()}:R>.");
    }

    [Command("setbanner")]
    [Summary("Change the server's banner to the image at a given URL, and set BannerMode to None.")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("url", ParameterType.String, "URL to an image file")]
    [Example(".setbanner https://manebooru.art/images/404")]
    public async Task SetBannnerCommandAsync([Remainder] string url = "")
    {
        var cleanUrl = url.Trim();
        try
        {
            await DiscordHelper.SetBannerToUrlImage(cleanUrl, new SocketGuildAdapter(Context.Guild));
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to set banner: [{ex.GetType().Name}] {ex.Message}\n{ex.StackTrace}";
            await ReplyAsync(errorMsg);
            _logger.Log(errorMsg);
            return;
        }

        var msg = $"Set banner to <{cleanUrl}>";
        if (_config.BannerMode != BannerMode.None)
        {
            _config.BannerMode = BannerMode.None;
            await FileHelper.SaveConfigAsync(_config);

            msg += " and reset BannerMode to None so it won't change back";
        }
        await ReplyAsync(msg);
    }

    [Command("recentmessages")]
    [Summary("Dump all of the recent messages Izzy has cached for a specific user.")]
    [Remarks(
        "This command is useful because some Discord message deletions (most importantly: banning with deletions) do not produce DeletedMessage events, and thus Izzy won't know to log them in LogChannel. This cache is also an implementation detail of some of Izzy's other systems.\n" +
        "- Izzy will cache at least `.config RecentMessagesPerUser` messages for each user\n" +
        "- Izzy will not throw away a message while it remains relevant for spam pressure calculations (see SpamPressureDecay, SpamMaxPressure and SpamBasePressure)\n" +
        "- Edits and deletes are ignored; only the original version of the message is cached\n" +
        "- Restarting Izzy clears this cache\n" +
        "- Messages in `ModChannel` are ignored"
    )]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("user", ParameterType.UserResolvable, "The user to show messages for")]
    [Example(".recentmessages @Izzy Moonbot")]
    public async Task RecentMessagesCommandAsync([Remainder] string user = "")
    {
        var (userId, userError) = await ParseHelper.TryParseUserResolvable(user, new SocketGuildAdapter(Context.Guild));
        if (userId == null)
        {
            await ReplyAsync($"I couldn't find that user's id: {userError}");
            return;
        }

        if (
            !_state.RecentMessages.TryGetValue((ulong)userId, out var recentMessages) ||
            recentMessages.Count == 0
        ) {
            await ReplyAsync($"I haven't seen any messages from <@{userId}> since my last restart. Sorry.", allowedMentions: AllowedMentions.None);
            return;
        }

        await ReplyAsync(
            $"These are all the recent messages (without edits or deletions) I have cached from <@{userId}>:\n" +
            "\n" +
            String.Join("\n", recentMessages.Select(rm => $"[{rm.GetJumpUrl()} <t:{rm.Timestamp.ToUnixTimeSeconds()}:R>] {rm.Content}")),
            allowedMentions: AllowedMentions.None
        );
    }
}
