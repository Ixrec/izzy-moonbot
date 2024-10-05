using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Izzy_Moonbot.Settings;
using Izzy_Moonbot.Adapters;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Text.RegularExpressions;
using Flurl.Http;

namespace Izzy_Moonbot.Helpers;

public static class DiscordHelper
{
    public readonly static int MessageLengthLimit = 2000;

    // These setters should only be used by tests
    public static ulong? DefaultGuildId { get; set; } = null;
    public static List<ulong>? DevUserIds { get; set; } = null;
    public static bool PleaseAwaitEvents { get; set; } = false;

    // In production code, our event handlers need to return immediately no matter how
    // much work there is to do, or else we "block the gateway task".
    // But in tests we need to wait for that work to complete.
    public static async Task<object?> LeakOrAwaitTask(Task t)
    {
        if (PleaseAwaitEvents)
            await t;
        return Task.CompletedTask;
    }

    public static bool ShouldExecuteInPrivate(bool externalUsageAllowedFlag, SocketCommandContext context)
    {
        return ShouldExecuteInPrivate(externalUsageAllowedFlag, new SocketCommandContextAdapter(context));
    }
    public static bool ShouldExecuteInPrivate(bool externalUsageAllowedFlag, IIzzyContext context)
    {
        if (context.IsPrivate || context.Guild?.Id != DefaultGuild())
        {
            return externalUsageAllowedFlag;
        }
        
        return true;
    }

    public static bool IsDefaultGuild(SocketCommandContext context)
    {
        return IsDefaultGuild(new SocketCommandContextAdapter(context));
    }
    public static bool IsDefaultGuild(IIzzyContext context)
    {
        if (context.IsPrivate) return false;
        
        return context.Guild?.Id == DefaultGuild();
    }
    
    public static ulong DefaultGuild()
    {
        var maybeDefaultGuildId = DefaultGuildId;
        if (maybeDefaultGuildId is ulong defaultGuildId)
            return defaultGuildId;

        try
        {
            var settings = GetDiscordSettings();
            return settings.DefaultGuild;
        }
        catch (FileNotFoundException e)
        {
            Console.WriteLine("Caught FileNotFoundException in DefaultGuild(). " +
                "If you're seeing this in tests, you probably forgot to set a fake DefaultGuildId.");
            Console.WriteLine(e.Message);
            throw;
        }
    }
    
    public static bool IsDev(ulong user)
    {
        var maybeDevUserIds = DevUserIds;
        if (maybeDevUserIds is List<ulong> devUserIds)
            return devUserIds.Any(userId => userId == user);

        var settings = GetDiscordSettings();
        return settings.DevUsers.Any(userId => userId == user);
    }

    public static DiscordSettings GetDiscordSettings()
    {
        var config = new ConfigurationBuilder()
            #if DEBUG
            .AddJsonFile("appsettings.Development.json")
            #else
            .AddJsonFile("appsettings.json")
            #endif
            .Build();

        var section = config.GetSection(nameof(DiscordSettings));
        var settings = section.Get<DiscordSettings>();
        
        if (settings == null) throw new NullReferenceException("Discord settings is null!");

        return settings;
    }
    
    public static bool IsProcessableMessage(IIzzyMessage msg)
    {
        if (msg.Type != MessageType.Default && msg.Type != MessageType.Reply &&
            msg.Type != MessageType.ThreadStarterMessage) return false;
        return true;
    }

    public static string StripQuotes(string str)
    {
        var quotes = new[]
        {
            '"', '\'', 'ʺ', '˝', 'ˮ', '˶', 'ײ', '״', '᳓', '“', '”', '‟', '″', '‶', '〃', '＂'
        };

        var needToStrip = str.Length > 0 && quotes.Contains(str.First()) && quotes.Contains(str.Last());

        return needToStrip ? str[new Range(1, ^1)] : str;
    }

    public static bool IsSpace(char character)
    {
        return character is ' ' or '\t' or '\r';
    }

    public static object? GetSafely<T>(IEnumerable<T> array, int index)
    {
        if (array.Count() <= index) return null;
        if (index < 0) return null;

        return array.ElementAt(index);
    }

    // The return value is (nextArgString, remainingArgsIfAny)
    // string.Split() does not suffice because we want to support quoted arguments
    public static (string?, string?) GetArgument(string args)
    {
        Func<string, string> trimQuotes = arg =>
        {
            return (arg[0] == '"' && arg.Last() == '"') ?
                arg[1..(arg.Length - 1)] :
                arg;
        };

        var betweenQuotes = false;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];

            // start or end a quoted argument only if that quote is unescaped
            // (and \ only has this special meaning when preceding ")
            if ((c == '"') && (i <= 1 || args[i-1] != '\\'))
                betweenQuotes = !betweenQuotes;

            // found a space outside quotes; that means we've reached the end of the current arg
            if (IsSpace(c) && !betweenQuotes)
            {
                var nextArg = args.Substring(0, i);

                var endOfSpaceRun = i;
                while (endOfSpaceRun < args.Length && IsSpace(args[endOfSpaceRun]))
                    endOfSpaceRun++;

                return (endOfSpaceRun >= args.Length) ?
                    // If there are only spaces after the arg, then it's the last arg if any
                    (nextArg == "") ?
                        (null, null) :
                        (trimQuotes(nextArg), null) :
                    // otherwise there are more args left to parse with future GetArgument() calls
                    (nextArg == "") ?
                        // if this "arg" was the empty string, recurse so caller gets the first non-empty arg if any
                        GetArgument(args.Substring(endOfSpaceRun)) :
                        (trimQuotes(nextArg), args.Substring(endOfSpaceRun));
            }
        }

        // If we never found a space (outside a quoted arg), then the whole string is at most one arg
        return (args == "") ?
            (null, null) :
            (trimQuotes(args), null);
    }

    public static ArgumentResult GetArguments(string content)
    {
        var arguments = new List<string>();
        var indices = new List<int>();

        var (nextArg, remainingArgs) = GetArgument(content);
        if (nextArg != null)
        {
            arguments.Add(nextArg);
            if (remainingArgs != null)
                indices.Add(content.Length - remainingArgs.Length);
            else
                indices.Add(content.Length);

            while (remainingArgs != null)
            {
                var remainingLength = remainingArgs!.Length;

                (nextArg, remainingArgs) = GetArgument(remainingArgs);
                if (nextArg != null)
                {
                    arguments.Add(nextArg);
                    if (remainingArgs != null)
                        indices.Add(indices.Last() + (remainingLength - remainingArgs.Length));
                    else
                        indices.Add(content.Length);
                }
            }
        }

        return new ArgumentResult
        {
            Arguments = arguments.ToArray(),
            Indices = indices.ToArray()
        };
    }

    public struct ArgumentResult
    {
        public string[] Arguments;
        public int[] Indices;
    }

    public static bool IsInGuild(IIzzyMessage msg)
    {
        if (msg.Channel.GetChannelType() == ChannelType.DM ||
            msg.Channel.GetChannelType() == ChannelType.Group) return false;
        return true;
    }

    // Where "Discord whitespace" refers to Char.IsWhiteSpace as well as the ":blank:" emoji
    public static string TrimDiscordWhitespace(string wholeString)
    {
        List<string> singleCharacterOrEmojiOfWhitespace = [
            @"\s",
            @":blank:",
            @"<:blank:[0-9]+>"
        ];
        var runOfDiscordWhitespace = $"({ string.Join("|", singleCharacterOrEmojiOfWhitespace)})+";

        var leadingWhitespaceRegex = new Regex($"^{runOfDiscordWhitespace}");
        var trailingWhitespaceRegex = new Regex($"{runOfDiscordWhitespace}$");

        var s = wholeString;
        if (leadingWhitespaceRegex.Matches(s).Any())
            s = leadingWhitespaceRegex.Replace(s, "");
        if (trailingWhitespaceRegex.Matches(s).Any())
            s = trailingWhitespaceRegex.Replace(s, "");

        return s;
    }

    public static bool WithinLevenshteinDistanceOf(string source, string target, uint maxDist)
    {
        // only null checks are necessary here, but we might as well early return on empty strings too
        if (String.IsNullOrEmpty(source) && String.IsNullOrEmpty(target))
            return true;
        if (String.IsNullOrEmpty(source))
            return target.Length <= maxDist;
        if (String.IsNullOrEmpty(target))
            return source.Length <= maxDist;

        // The idea is that after j iterations of the main loop, we want:
        // currDists[i] == LD(s[0..i], t[0..j+1])
        // prevDists[i] == LD(s[0..i], t[0..j])
        // So when the loop is over LD(s, t) == currDists[s.Length]

        int[] currDists = new int[source.Length + 1];
        int[] prevDists = new int[source.Length + 1];

        // For the j == 0 base case, there are no prevDists yet, but
        // LD(s[0..i], t[0..j]) == LD(s[0..i], "") == i so that's easy.
        // Set these to currDists so the initial swap puts them in prevDists
        for (int i = 0; i <= source.Length; i++) { currDists[i] = i; }

        // actually compute LD(s[0..i], t[0..j+1]) for every i and j
        for (int j = 0; j < target.Length; j++)
        {
            int[] swap = prevDists;
            prevDists = currDists;
            currDists = swap;

            currDists[0] = j + 1; // i == 0 base case: LD(s[0..0], t[0..j+1]) == j+1

            for (int i = 0; i < source.Length; i++)
            {
                int deletion = currDists[i] + 1;      // if s[i+1] gets deleted,          then LD(s[0..i+1], t[0..j]) == 1        + LD(s[0..i],   t[0..j])
                                                      // example:                              LD("Izzy",    "Izz")   == 1        + LD("Izz",     "Izz") = 1
                int insertion = prevDists[i + 1] + 1; // if t[j] gets inserted at s[i+1], then LD(s[0..i+1], t[0..j]) == 1        + LD(s[0..i+1], t[0..j-1])
                                                      // example:                              LD("Izz",     "Izzy")  == 1        + LD("Izz",     "Izz") = 1
                int substitution = prevDists[i] +     // if s[i+1] gets set to t[j],      then LD(s[0..i+1], t[0..j]) == (0 or 1) + LD(s[0..i],   t[0..j-1])
                    (source[i] == target[j] ? 0 : 1); // example:                              LD("Izzz",    "Izzy")  == 1        + LD("Izz",     "Izz") = 1
                                                      //                                       LD("Izzy",    "Izzy")  == 0        + LD("Izz",     "Izz") = 0

                currDists[i + 1] = Math.Min(deletion, Math.Min(insertion, substitution));
            }

            // if all of currDists is already too high, then we know the final LD will be too
            if (currDists.Min() > maxDist) return false;
        }

        var actualLevenshteinDistance = currDists[source.Length];
        return actualLevenshteinDistance <= maxDist;
    }

    public static async Task SetBannerToUrlImage(string url, IIzzyGuild guild)
    {
        Stream stream = await url
            .WithHeader("user-agent", $"Izzy-Moonbot (Linux x86_64) Flurl.Http/3.2.4 DotNET/8.0")
            .GetStreamAsync();

        var image = new Image(stream);

        await guild.SetBanner(image);
    }

    public static async Task<string> AuditLogForCommand(SocketCommandContext context)
    {
        return await AuditLogForCommand(new SocketCommandContextAdapter(context));
    }
    public static async Task<string> AuditLogForCommand(IIzzyContext context)
    {
        var user = context.User;
        var channel = context.Channel;
        var now = DateTimeHelper.UtcNow;

        // note that newlines, markdown, mentions, etc. aren't applied in audit log messages,
        // but some of them are still useful to make the message clearer
        return $"Command `{context.Message.Content}` " +
            $"was run by {DisplayName(user, context.Guild)} ({user.Username}/{user.Id}) " +
            $"in #{channel.Name} (<#{channel.Id}>) " +
            $"at {now} (<t:{now.ToUnixTimeSeconds()}>)";
    }

    // For a GuildUser, the .DisplayName property appears to use
    //    .Nickname -> .GlobalName -> .Username
    // in that order.
    // This method helps ensure we consistently guild/member-ify our users if it's
    // possible to do so, and if not we still do .GlobalName -> .Username at least.
    public static string DisplayName(IUser user, SocketGuild? guild)
    {
        return DisplayName(new DiscordNetUserAdapter(user), guild != null ? new SocketGuildAdapter(guild) : null);
    }
    public static string DisplayName(IIzzyUser user, IIzzyGuild? guild)
    {
        var member = guild?.GetUser(user.Id);
        return member != null ? member.DisplayName : user.GlobalName ?? user.Username;
    }

    // In Discord, <> angle brackets around a url prevent it from being automatically unfurled.
    // Izzy often wants to identify urls in a message that *will* unfurl, so we need a reliable way
    // to identify urls that aren't enclosed by <>.
    // This is essentially https://stackoverflow.com/a/3809435 with added lookaround for <>s.
    public static Regex UnfurlableUrl =
        new(@"(?<!<)(https?://(www\.)?[-a-zA-Z0-9@:%._+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_+.~#?&//=]*))(?!>)");

    public static ulong BanishedRoleId = 368961099925553153ul;
}
