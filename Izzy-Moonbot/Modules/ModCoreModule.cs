using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Izzy_Moonbot.Adapters;
using Izzy_Moonbot.Attributes;
using Izzy_Moonbot.Describers;
using Izzy_Moonbot.Helpers;
using Izzy_Moonbot.Service;
using Izzy_Moonbot.Settings;
using Microsoft.Extensions.Logging;

namespace Izzy_Moonbot.Modules;

[Summary("The need-to-know moderator-only commands.")]
public class ModCoreModule : ModuleBase<SocketCommandContext>
{
    private readonly LoggingService _logger;
    private readonly Config _config;
    private readonly ScheduleService _schedule;
    private readonly Dictionary<ulong, User> _users;
    private readonly ModService _mod;
    private readonly ConfigDescriber _configDescriber;

    public ModCoreModule(LoggingService logger, Config config, Dictionary<ulong, User> users,
        ScheduleService schedule, ModService mod, ConfigDescriber configDescriber)
    {
        _logger = logger;
        _config = config;
        _schedule = schedule;
        _users = users;
        _mod = mod;
        _configDescriber = configDescriber;
    }

    [Command("config")]
    [Summary("Inspect or modify one of Izzy's configuration items")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("key", ParameterType.String, "The config item to get/modify. This is case sensitive.")]
    [Parameter("[...]", ParameterType.String, "...", true)]
    public async Task ConfigCommandAsync(
        [Summary("The item to get/modify.")] string configItemKey = "",
        [Summary("")][Remainder] string? value = "")
    {
        await ConfigCommand.TestableConfigCommandAsync(
            new SocketCommandContextAdapter(Context),
            _config,
            _configDescriber,
            configItemKey,
            value
        );
    }

    [Command("resolveuser")]
    [Summary("Resolve a user name, id or mention/ping to a specific user, then print that user's id and mention.")]
    [Remarks("This is designed for mobile moderation. In particular, it puts the id and mention in two separate messages with no other text so that long tap -> Copy Text just works.")]
    [Alias("getid", "getping")]
    [Parameter("user", ParameterType.UserResolvable, "The user to get an id and mention for, or yourself if not provided.", true)]
    public async Task ResolveUserCommandAsync(
        [Remainder] string user = "")
    {
        if (user == "") user = Context.User.Id.ToString(); // Set to user ID to target self.

        var (userId, userError) = await ParseHelper.TryParseUserResolvable(user, new SocketGuildAdapter(Context.Guild));
        if (userId == null)
        {
            await ReplyAsync($"I couldn't find that user's id: {userError}");
            return;
        }

        await ReplyAsync(userId.ToString(), allowedMentions: AllowedMentions.None);

        await ReplyAsync($"<@{userId}>", allowedMentions: AllowedMentions.None);
    }

    [Command("userinfo")]
    [Summary("Get information about a user (or yourself)")]
    [Remarks("In the Discord UI, right-click on a user's name and go to 'Apps' for an alternative way of invoking this command.")]
    [Alias("uinfo")]
    [Parameter("user", ParameterType.UserResolvable, "The user to get information about, or yourself if not provided.", true)]
    public async Task UserInfoCommandAsync(
        [Remainder] string user = "")
    {
        if (user == "") user = Context.User.Id.ToString(); // Set to user ID to target self.

        var (userId, userError) = await ParseHelper.TryParseUserResolvable(user, new SocketGuildAdapter(Context.Guild));
        if (userId == null)
        {
            await ReplyAsync($"I couldn't find that user's id: {userError}");
            return;
        }

        var output = await UserInfoImpl(Context.Client, Context.Guild.Id, (ulong)userId, _users);

        await ReplyAsync(output, allowedMentions: AllowedMentions.None);
    }

    static public async Task<string> UserInfoImpl(DiscordSocketClient client, ulong guildId, ulong userId, Dictionary<ulong, User> users)
    {
        var output = $"";

        var guild = client.GetGuild(guildId);
        var member = guild.GetUser(userId);
        if (member == null)
        {
            // Even if `userId` is not a member of Manechat, it might still be a real user elsewhere in Discord
            var discordUser = await client.GetUserAsync(userId);

            if (discordUser == null)
                return "I couldn't find that user, sorry!";

            output += $"**User:** `<@{discordUser.Id}>` {DiscordHelper.DisplayName(discordUser, guild)} ({discordUser.Username}/{discordUser.Id})\n";
            output += $"**{guild.Name} Server Nickname**: None (user isn't in this server)\n";
            output += $"**Global Display Name**: {discordUser.GlobalName ?? "None"}\n";
            output += users.ContainsKey(discordUser.Id)
                ? $"**All Known Names:** {string.Join(", ", users[discordUser.Id].Aliases)}\n"
                : $"**All Known Names:** None (user isn't known by Izzy)\n";
            output += $"**Roles:** None (user isn't in this server)\n";
            output += "**History:** ";
            output += $"Created <t:{discordUser.CreatedAt.ToUnixTimeSeconds()}:R>";
            output += $"**Avatar(s):** \n";
            output += $"    Server: User is not in this server.\n";
            output += $"    Global: {discordUser.GetAvatarUrl() ?? "No global avatar found."}";
        }
        else
        {
            output += $"**User:** `<@{member.Id}>` {member.DisplayName} ({member.Username}/{member.Id})\n";
            output += $"**{guild.Name} Server Nickname**: {member.Nickname ?? "None"}\n";
            output += $"**Global Display Name**: {member.GlobalName ?? "None"}\n";
            output += $"**All Known Names:** {string.Join(", ", users[member.Id].Aliases)}\n";
            output +=
                $"**Roles:** {string.Join(", ", member.Roles.Where(role => role.Id != guildId).Select(role => role.Name))}\n";
            output += $"**History:** ";
            output += $"Created <t:{member.CreatedAt.ToUnixTimeSeconds()}:R>";
            if (member.JoinedAt.HasValue)
            {
                output +=
                    $", joined <t:{member.JoinedAt.Value.ToUnixTimeSeconds()}:R>";
            }

            output += $"**Avatar(s):** \n";
            output += $"    Server: {member.GetGuildAvatarUrl() ?? "No server avatar found."}\n";
            output += $"    Global: {member.GetAvatarUrl() ?? "No global avatar found."}";
        }

        return output;
    }

    [Command("ban")]
    [Summary(
        "Bans a user without deleting message history, and optionally schedules unbanning them at a later date. If the user is already banned, this will only schedule unbanning.")]
    [Remarks("Note that an \"indefinite\" ban, with no scheduled unban, is the same as banning with the regular Discord UI.")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("user", ParameterType.UnambiguousUser, "The user to ban.")]
    [Parameter("duration", ParameterType.DateTime, "How long the ban should last, e.g. \"2 weeks\" or \"6 months\". Omit for an indefinite ban.", true)]
    [Example(".ban 123456789012345678 1 month")]
    public async Task BanCommandAsync(
        [Remainder] string argsString = "")
    {
        await TestableBanCommandAsync(
            new SocketCommandContextAdapter(Context),
            argsString
        );
    }

    public async Task TestableBanCommandAsync(
        IIzzyContext Context,
        string argsString = "",
        bool generateUserlogTemplate = true)
    {
        if (argsString == "")
        {
            await Context.Channel.SendMessageAsync($"Please provide a user to ban. Refer to `{_config.Prefix}help ban` for more information.");
            return;
        }

        if (ParseHelper.TryParseUnambiguousUser(argsString, out var userErrorString) is not var (userId, argsAfterUser))
        {
            await Context.Channel.SendMessageAsync($"Failed to get a user id from the first argument: {userErrorString}");
            return;
        }
        var member = Context.Guild?.GetUser(userId);

        ParseDateTimeResult? time = null;
        if (argsAfterUser.Trim() != "")
        {
            time = ParseHelper.TryParseDateTime(argsAfterUser, out var parseError)?.Item1;
            if (time is null)
            {
                await Context.Channel.SendMessageAsync($"Failed to comprehend time: {parseError}");
                return;
            }
            if (time.RepeatType is not ScheduledJobRepeatType.None)
            {
                await Context.Channel.SendMessageAsync("I can't ban a user repeatedly! Please give me a time that isn't repeating.");
                return;
            }
        }

        if (userId == Context.Client.CurrentUser.Id)
        {
            var rnd = new Random();
            if (rnd.Next(100) == 0)
            {
                await Context.Channel.SendMessageAsync("<:izzydeletethis:1028964499723661372>");
            }
            else if (rnd.NextSingle() > 0.5)
            {
                await Context.Channel.SendMessageAsync("<:sweetiebroken:399725081674383360>");
            }
            else
            {
                await Context.Channel.SendMessageAsync("<:izzysadness:910198257702031362>");
            }

            return;
        }

        if (member != null && member.Roles.Select(role => role.Id).Contains(_config.ModRole))
        {
            await Context.Channel.SendMessageAsync("I can't ban a mod. <:izzynothoughtsheadempty:910198222255972382>");
            return;
        }

        if (member != null && member.Hierarchy >= Context.Guild?.GetUser(Context.Client.CurrentUser.Id)?.Hierarchy)
        {
            await Context.Channel.SendMessageAsync(
                "That user is either at the same level or higher than me in the role hierarchy, I cannot ban them. <:izzynothoughtsheadempty:910198222255972382>");
            return;
        }

        // Okay, enough joking around, serious Izzy time
        var reason = await DiscordHelper.AuditLogForCommand(Context);
        var hasExistingBan = await Context.Guild!.GetIsBannedAsync(userId);

        if (!hasExistingBan)
        {
            // No ban exists, very serious Izzy time.
            try
            {
                await Context.Guild.AddBanAsync(userId, pruneDays: 0, reason: reason );
            }
            catch (Exception ex)
            {
                _logger.Log(
                    $"AddBanAsync() threw an exception when trying to ban userId {userId}\n" +
                    $"Type: {ex.GetType().Name}\n" +
                    $"Message: {ex.Message}\n" +
                    $"Stack Trace: {ex.StackTrace}");

                await Context.Channel.SendMessageAsync(
                    $":warning: Failed to ban userId {userId}\n" +
                    $"Error was: [{ex.GetType().Name}] {ex.Message}\n" +
                    $"(Check Izzy's logs for a full stack trace)");

                return;
            }

            if (time != null)
            {
                // Create scheduled task!
                var action = new ScheduledUnbanJob(userId, reason);
                var job = new ScheduledJob(DateTimeHelper.UtcNow, time.Time, action);
                await _schedule.CreateScheduledJob(job);
            }

            var msg = $"<:izzydeletethis:1028964499723661372> I've banned {(member == null ? $"<@{userId}>" : member.DisplayName)} ({userId}).{(time != null ? $" They'll be unbanned <t:{time.Time.ToUnixTimeSeconds()}:R>." : "")}";
            if (generateUserlogTemplate)
            {
                msg += "\n\n" +
                    $"Here's a userlog I unicycled that you can use if you want to!\n```\n" +
                    $"Type: Ban ({(argsAfterUser == "" ? "" : $"{argsAfterUser} ")}{(time == null ? "Indefinite" : $"<t:{time.Time.ToUnixTimeSeconds()}:R>")})\n" +
                    $"User: <@{userId}> ({userId})\n" +
                    $"Names: {(_users.ContainsKey(userId) ? string.Join(", ", _users[userId].Aliases) : "None (user isn't known by Izzy)")}\n" +
                    $"```";
            }
            await Context.Channel.SendMessageAsync(msg);
        }
        else
        {
            var getUserUnban = new Func<ScheduledJob, bool>(job =>
                job.Action is ScheduledUnbanJob unbanJob &&
                unbanJob.User == userId);

            // ban exists, make sure a time is declared
            if (time == null)
            {
                // time not declared, make ban permanent.
                if (_schedule.GetScheduledJobs(getUserUnban).Any())
                {
                    var job = _schedule.GetScheduledJobs(getUserUnban).First();

                    await _schedule.DeleteScheduledJob(job);

                    var msg = $"This user is already banned. I have removed an existing unban for them which was scheduled <t:{job.ExecuteAt.ToUnixTimeSeconds()}:R>.";
                    if (generateUserlogTemplate)
                    {
                        msg += "\n\n" +
                            $"Here's a userlog I unicycled that you can use if you want to!\n```\n" +
                            $"Type: Ban (Indefinite)\n" +
                            $"User: <@{userId}> ({(member != null ? $"{member.Username}/" : "")}{userId})\n" +
                            $"Names: {(_users.ContainsKey(userId) ? string.Join(", ", _users[userId].Aliases) : "None (user isn't known by Izzy)")}\n" +
                            $"```";
                    }
                    await Context.Channel.SendMessageAsync(msg);
                }
                else
                {
                    // Doesn't exist, it's already permanent.
                    await Context.Channel.SendMessageAsync("This user is already banned, with no scheduled unban. No changes made.");
                }

                return;
            }

            // time declared, make ban temporary.
            if (_schedule.GetScheduledJobs(getUserUnban).Any())
            {
                var jobs = _schedule.GetScheduledJobs(getUserUnban);

                jobs.Sort((job1, job2) =>
                {
                    if (job1.ExecuteAt.ToUnixTimeMilliseconds() < job2.ExecuteAt.ToUnixTimeMilliseconds())
                    {
                        return -1;
                    }
                    return job1.ExecuteAt.ToUnixTimeMilliseconds() > job2.ExecuteAt.ToUnixTimeMilliseconds() ? 1 : 0;
                });

                var job = jobs[0];
                var jobOriginalExecution = job.ExecuteAt.ToUnixTimeSeconds();

                job.ExecuteAt = time.Time;

                await _schedule.ModifyScheduledJob(job.Id, job);

                var msg = $"This user is already banned. I have modified an existing scheduled unban for them from <t:{jobOriginalExecution}:R> to <t:{job.ExecuteAt.ToUnixTimeSeconds()}:R>.";
                if (generateUserlogTemplate)
                {
                    msg += "\n\n" +
                        $"Here's a userlog I unicycled that you can use if you want to!\n```\n" +
                        $"Type: Ban ({argsAfterUser} <t:{time.Time.ToUnixTimeSeconds()}:R>)\n" +
                        $"User: <@{userId}> ({(member != null ? $"{member.Username}/" : "")}{userId})\n" +
                        $"Names: {(_users.ContainsKey(userId) ? string.Join(", ", _users[userId].Aliases) : "None (user isn't known by Izzy)")}\n" +
                        $"```";
                }
                await Context.Channel.SendMessageAsync(msg);
            } 
            else
            {
                // Doesn't exist, it needs to exist.
                // Create scheduled task!
                var action = new ScheduledUnbanJob(userId, reason);
                var job = new ScheduledJob(DateTimeHelper.UtcNow, time.Time, action);
                await _schedule.CreateScheduledJob(job);

                var msg = $"This user is already banned. I have scheduled an unban for this user. They'll be unbanned <t:{time.Time.ToUnixTimeSeconds()}:R>";
                if (generateUserlogTemplate)
                {
                    msg += "\n\n" +
                        $"Here's a userlog I unicycled that you can use if you want to!\n```\n" +
                        $"Type: Ban ({argsAfterUser} <t:{time.Time.ToUnixTimeSeconds()}:R>)\n" +
                        $"User: <@{userId}> ({(member != null ? $"{member.Username}/" : "")}{userId})\n" +
                        $"Names: {(_users.ContainsKey(userId) ? string.Join(", ", _users[userId].Aliases) : "None (user isn't known by Izzy)")}\n" +
                        $"```";
                }
                await Context.Channel.SendMessageAsync(msg);
            }
        }
    }

    [Command("banall")]
    [Summary("Bans any number of users in a single command without deleting message history.")]
    [Remarks("This prints a separate message for every user, so it can get spammy. Timed bans are not supported; use the regular .ban if you need those.")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("user(s)", ParameterType.UnambiguousUser, "The user(s) to ban.")]
    [Example(".ban 111 112 113 114 115")]
    public async Task BanAllCommandAsync([Remainder] string usersString = "")
    {
        await TestableBanAllCommandAsync(
            new SocketCommandContextAdapter(Context),
            usersString
        );
    }

    public async Task TestableBanAllCommandAsync(
        IIzzyContext context,
        string usersString = "")
    {
        if (usersString == "")
        {
            await context.Channel.SendMessageAsync($"Who do you want me to ban?");
            return;
        }

        var userArgs = usersString.Split(new char[] { ' ', '\r', '\n', '\t', '\v' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var userArg in userArgs)
        {
            await TestableBanCommandAsync(context, userArg, false /* generateUserlogTemplate */);
        }
    }

    [Command("assignrole")]
    [Summary(
        "Assigns a role to a user, and optionally schedules removing that role. If the user already has that role, this will only schedule removal.")]
    [Remarks("Note that an \"indefinite\" role assignment, with no scheduled removal, is the same as assigning a role with the regular Discord UI.")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("role", ParameterType.Role, "The role to assign.")]
    [Parameter("duration", ParameterType.DateTime, "How long the role should last, e.g. \"2 weeks\" or \"6 months\". Omit for an indefinite role assignment.", true)]
    [Parameter("user", ParameterType.UnambiguousUser, "The user to assign the role.")]
    [Example(".assignrole @Best Pony 24 hours @Izzy Moonbot")]
    public async Task AssignRoleCommandAsync(
        [Remainder] string argsString = "")
    {
        await TestableAssignRoleCommandAsync(
            new SocketCommandContextAdapter(Context),
            argsString
        );
    }

    public async Task TestableAssignRoleCommandAsync(
        IIzzyContext context,
        string argsString = "")
    {
        if (argsString == "")
        {
            await context.Channel.SendMessageAsync($"Please provide a user and a role to assign. Refer to `{_config.Prefix}help assignrole` for more information.");
            return;
        }

        if (ParseHelper.TryParseRoleResolvable(argsString, context.Guild!, out var roleParseError) is not var (roleId, argsAfterRole))
        {
            await context.Channel.SendMessageAsync($"I couldn't find that role, sorry! {roleParseError}");
            return;
        }
        var role = context.Guild?.GetRole(roleId);

        ParseDateTimeResult? time = null;
        string? argsAfterTime = null;
        if (argsAfterRole.Trim() != "")
        {
            var timeResult = ParseHelper.TryParseDateTime(argsAfterRole, out var parseError);
            if (timeResult is null)
            {
                argsAfterTime = argsAfterRole;
            }
            else
            {
                time = timeResult?.Item1;
                argsAfterTime = timeResult?.Item2;
                if (time?.RepeatType is not ScheduledJobRepeatType.None)
                {
                    await context.Channel.SendMessageAsync("I can't assign a role repeatedly! Please give me a time that isn't repeating.");
                    return;
                }
            }
        }

        if (argsAfterTime is null)
        {
            await context.Channel.SendMessageAsync("I need a user to assign the role to.");
            return;
        }
        if (ParseHelper.TryParseUnambiguousUser(argsAfterTime, out var userErrorString) is not var (userId, argsAfterUser))
        {
            await Context.Channel.SendMessageAsync($"Failed to get a user id from the last argument: {userErrorString}");
            return;
        }
        var maybeMember = context.Guild?.GetUser(userId);

        if (maybeMember is IIzzyGuildUser member)
        {
            if (role?.Position >= context.Guild?.GetUser(context.Client.CurrentUser.Id)?.Hierarchy)
            {
                await context.Channel.SendMessageAsync(
                    "That role is either at the same level or higher than me in the role hierarchy, I cannot assign it. <:izzynothoughtsheadempty:910198222255972382>");
                return;
            }

            // Actually add the role, if they don't have it already.
            var alreadyHasRole = member.Roles.Select(role => role.Id).Contains(roleId);
            if (!alreadyHasRole)
            {
                await _mod.AddRoles(member, new[] { roleId }, await DiscordHelper.AuditLogForCommand(context));
            }

            var message = alreadyHasRole ? $"<@{userId}> already has that role." : $"I've given <@&{roleId}> to <@{userId}>.";

            // Delete any existing scheduled removals for this user and role
            var getRoleRemoval = new Func<ScheduledJob, bool>(job =>
                job.Action is ScheduledRoleRemovalJob removalJob &&
                removalJob.User == member.Id &&
                removalJob.Role == roleId);

            var hasExistingRemovalJob = _schedule.GetScheduledJobs(getRoleRemoval).Any();
            if (hasExistingRemovalJob)
            {
                var jobs = _schedule.GetScheduledJobs(getRoleRemoval);
                foreach (var job in jobs)
                {
                    await _schedule.DeleteScheduledJob(job);
                }
            }

            // If a duration was provided, schedule removal.
            if (time is not null)
            {
                _logger.Log($"Adding scheduled job to remove role {roleId} from user {userId} at {time.Time}", level: LogLevel.Debug);
                var action = new ScheduledRoleRemovalJob(roleId, member.Id, await DiscordHelper.AuditLogForCommand(context));
                var task = new ScheduledJob(DateTimeHelper.UtcNow, time.Time, action);
                await _schedule.CreateScheduledJob(task);
                _logger.Log($"Added scheduled job for new user", level: LogLevel.Debug);

                if (hasExistingRemovalJob)
                {
                    message += $" I've replaced an existing removal job with a new one scheduled <t:{time.Time.ToUnixTimeSeconds()}:R>.";
                }
                else
                {
                    message += $" I've scheduled a removal <t:{time.Time.ToUnixTimeSeconds()}:R>.";
                }
            }
            else
            {
                if (hasExistingRemovalJob)
                {
                    message += $" I've removed a previously scheduled role removal.";
                }
            }

            await context.Channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
        }
        else
        {
            await context.Channel.SendMessageAsync("I couldn't find that user, sorry!");
            return;
        }
    }

    [Command("wipe")]
    [Summary("Deletes all messages in a channel sent within a certain amount of time.")]
    [Remarks("Then posts a 'bulk deletion log' as a text file in LogChannel.")]
    [RequireContext(ContextType.Guild)]
    [ModCommand(Group = "Permissions")]
    [DevCommand(Group = "Permissions")]
    [Parameter("channel", ParameterType.Channel, "The channel to wipe.")]
    [Parameter("duration", ParameterType.DateTime, "How far back to wipe messages, e.g. \"5 minutes\" or \"10 days\". " +
        "Defaults to 24 hours. Note that Discord doesn't support bulk deleting messages older than 14 days.", true)]
    [Example(".wipe #the-moon 20 minutes")]
    public async Task WipeCommandAsync(
        [Remainder] string argsString = "")
    {
        if (argsString == "")
        {
            await ReplyAsync($"Please provide a channel to wipe. Refer to `{_config.Prefix}help wipe` for more information.");
            return;
        }

        if (ParseHelper.TryParseChannelResolvable(argsString, new SocketCommandContextAdapter(Context), out var channelParseError) is not var (channelId, argsAfterChannel))
        {
            await ReplyAsync($"Failed to get a channel: {channelParseError}");
            return;
        }

        var channel = Context.Guild.GetTextChannel(channelId);
        if (channel == null)
        {
            await ReplyAsync("I can't send a message there.");
            return;
        }

        var izzyGuildUser = Context.Guild.GetUser(Context.Client.CurrentUser.Id);
        var izzysPermsInChannel = izzyGuildUser.GetPermissions(channel);
        if (!izzysPermsInChannel.Has(ChannelPermission.ManageMessages))
        {
            await ReplyAsync("Sorry, I don't have permission to manage/delete messages there.");
            return;
        }

        DateTimeOffset? timeArg = null;
        if (argsAfterChannel.Trim() != "")
        {
            if (ParseHelper.TryParseInterval(argsAfterChannel, out var timeParseError, inThePast: true) is not var (parsedTimeArg, _remainingArguments))
            {
                await ReplyAsync($"Failed to comprehend time: {timeParseError}");
                return;
            }
            timeArg = parsedTimeArg;
        }

        // default to wiping the last 24 hours
        var wipeThreshold = timeArg ?? DateTimeHelper.UtcNow.AddHours(-24);

        if ((DateTimeHelper.UtcNow - wipeThreshold).TotalDays > 14)
        {
            await ReplyAsync("I can't delete messages older than 14 days, sorry!");
            return;
        }

        _logger.Log($"Parsed .wipe command arguments. Scanning for messages in channel {channel.Name} more recent than {wipeThreshold}");

        // Gather up all the messages we need to delete and log.

        var messageIdsToDelete = new List<ulong>();

        // We've seen GetMessagesAsync return the messages in both chronological and reverse chronological order,
        // so we need to remember the message creation times in order to do our own sorting at the end.
        var bulkDeletionLog = new List<(DateTimeOffset, string, string)>();

        var bulkDeletionLimit = 500;

        var recentMessages = (await channel.GetMessagesAsync(bulkDeletionLimit).FlattenAsync())
            .TakeWhile(m => m.CreatedAt > wipeThreshold);

        if (recentMessages.Count() == bulkDeletionLimit)
            await ReplyAsync($"Reached my hardcoded limit of {bulkDeletionLimit} messages.\n" +
                $"If there are even more recent messages that should've been deleted, please run .wipe again.");

        foreach (var message in recentMessages)
        {
            messageIdsToDelete.Add(message.Id);
            bulkDeletionLog.Add((
                message.CreatedAt,
                $"{message.Author.Username} ({message.Author.Id})",
                $"[{message.CreatedAt}] {message.Author.Username}: {message.Content}"
            ));
        }

        // Actually do the deletion
        var messagesToDeleteCount = messageIdsToDelete.Count;
        _logger.Log($"Deleting {messagesToDeleteCount} messages from channel {channel.Name}");
        await channel.DeleteMessagesAsync(messageIdsToDelete);

        // Finally, post a bulk deletion log in LogChannel
        var logChannelId = _config.LogChannel;
        if (logChannelId == 0)
        {
            await ReplyAsync("I can't post a bulk deletion log, because .config LogChannel hasn't been set.");
            return;
        }
        var logChannel = Context.Guild.GetTextChannel(logChannelId);
        if (logChannel == null)
        {
            await ReplyAsync("Something went wrong trying to access LogChannel.");
            return;
        }

        if (messagesToDeleteCount > 0)
        {
            _logger.Log($"Assembling a bulk deletion log from the content of {messagesToDeleteCount} deleted messages");
            bulkDeletionLog.Sort((x, y) => x.Item1.CompareTo(y.Item1));

            var bulkDeletionLogString = string.Join(
                "\n\n",
                bulkDeletionLog.Select(logElement => logElement.Item3)
            );

            var involvedUserDescriptions = string.Join(", ", bulkDeletionLog.Select(logElement => logElement.Item2).ToHashSet());

            var s = new MemoryStream(Encoding.UTF8.GetBytes(bulkDeletionLogString));
            var fa = new FileAttachment(s, $"{channel.Name}_bulk_deletion_log_{DateTimeHelper.UtcNow.ToString()}.txt");
            var bulkDeletionMessage = await logChannel.SendFileAsync(fa, $"Finished wiping {channel.Name}, here's the bulk deletion log involving {involvedUserDescriptions}:");

            await ReplyAsync($"Finished wiping {channel.Mention}. {messagesToDeleteCount} messages were deleted: {bulkDeletionMessage.GetJumpUrl()}");
        }
        else
        {
            await ReplyAsync($"I didn't find any messages that recent in {channel.Mention}. Deleted nothing.");
        }
    }
}
