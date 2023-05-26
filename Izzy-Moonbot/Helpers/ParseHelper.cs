using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Izzy_Moonbot.Settings;

namespace Izzy_Moonbot.Helpers;

public static class ParseHelper
{
    /*public static (ulong, string)? TryParseUnambiguousUser(string argsString, out string? errorString)
    {
        errorString = null;
        var args = DiscordHelper.GetArguments(argsString);
        if (!args.Arguments.Any())
        {
            errorString = $"empty or all-whitespace string \"{argsString}\" can't be a datetime";
            return null;
        }

        if (!userPing.Contains("<@") || !userPing.Contains(">"))
        {
            if (ulong.TryParse(userPing, out var result)) return result;
            return 0;
        }

        var frontTrim = userPing[2..];

        // Discord is sometimes weird and gives us a mention like <@ID> or <@!ID> seemingly randomly???
        if (userPing.Contains("!")) frontTrim = userPing[3..];

        var trim = frontTrim.Split('>', 2)[0];
        return ulong.Parse(trim);
    }
    public static (ulong, string)? TryParseUserResolvable(string argsString, out string? errorString)
    {

    }*/

    public static (ParseDateTimeResult, string)? TryParseDateTime(string argsString, out string? errorString)
    {
        errorString = null;

        var args = DiscordHelper.GetArguments(argsString);
        if (!args.Arguments.Any())
        {
            errorString = $"empty or all-whitespace string \"{argsString}\" can't be a datetime";
            return null;
        }

        if (args.Arguments[0] == "in")
        {
            var intervalArgsString = string.Join("", argsString.Skip(args.Indices[0]));
            if (TryParseInterval(intervalArgsString, out var intervalError) is var (dto, remainingArgs))
                return (new ParseDateTimeResult(dto, ScheduledJobRepeatType.None), remainingArgs);

            errorString = $"Failed to extract a date/time from the start of \"{argsString}\":\n" +
                $"Not a valid interval because: {intervalError}\n" +
                $"    Valid example: \"in 1 hour\"";
            return null;
        }
        else if (args.Arguments[0] == "at")
        {
            var timeArgsString = string.Join("", argsString.Skip(args.Indices[0]));
            if (TryParseTimeInput(timeArgsString, out var timeError) is var (dto, remainingArgs))
                return (new ParseDateTimeResult(dto, ScheduledJobRepeatType.None), remainingArgs);

            errorString = $"Failed to extract a date/time from the start of \"{argsString}\":\n" +
                $"Not a valid time because: {timeError}\n" +
                $"    Valid example: \"at 12:00 UTC+0\"";
            return null;
        }
        else if (args.Arguments[0] == "on")
        {
            var subArgsString = string.Join("", argsString.Skip(args.Indices[0]));
            if (TryParseWeekdayTime(subArgsString, out var weekdayError) is var (weekdayDto, weekdayRemainingArgs))
                return (new ParseDateTimeResult(weekdayDto, ScheduledJobRepeatType.None), weekdayRemainingArgs);
            if (TryParseAbsoluteDateTime(subArgsString, out var dateError) is var (dateDto, dateRemainingArgs))
                return (new ParseDateTimeResult(dateDto, ScheduledJobRepeatType.None), dateRemainingArgs);

            errorString = $"Failed to extract a date/time from the start of \"{argsString}\". Using \"on\" means either weekday + time or date + time, but:\n" +
                $"Not a valid weekday + time because: {weekdayError}\n" +
                $"    Valid example: \"on monday 12:00 UTC+0\"\n" +
                $"Not a valid date + time because: {dateError}\n" +
                $"    Valid example: \"on 1 jan 2020 12:00 UTC+0\"";
            return null;
        }
        else if (args.Arguments[0] == "every")
        {
            if (args.Arguments[1] == "day")
            {
                var dayArgsString = string.Join("", argsString.Skip(args.Indices[1]));
                if (TryParseTimeInput(dayArgsString, out var timeError) is var (timeDto, timeRemainingArgs))
                    return (new ParseDateTimeResult(timeDto, ScheduledJobRepeatType.Daily), timeRemainingArgs);

                errorString = $"Failed to extract a date/time from the start of \"{argsString}\":\n" +
                    $"Not a valid repeating time because: {timeError}\n" +
                    $"    Valid example: \"every day 12:00 UTC+0\"";
                return null;
            }
            else if (args.Arguments[1] == "week")
            {
                var weekArgsString = string.Join("", argsString.Skip(args.Indices[1]));
                if (TryParseWeekdayTime(weekArgsString, out var weekdayError) is var (weekdayDto, weekdayRemainingArgs))
                    return (new ParseDateTimeResult(weekdayDto, ScheduledJobRepeatType.Weekly), weekdayRemainingArgs);

                errorString = $"Failed to extract a date/time from the start of \"{argsString}\":\n" +
                    $"Not a valid repeating weekday + time because: {weekdayError}\n" +
                    $"    Valid example: \"every week monday 12:00 UTC+0\"";
                return null;
            }
            else if (args.Arguments[1] == "year")
            {
                var yearArgsString = string.Join("", argsString.Skip(args.Indices[1]));
                if (TryParseDayMonthTime(yearArgsString, out var dateError) is var (dateDto, dateRemainingArgs))
                    return (new ParseDateTimeResult(dateDto, ScheduledJobRepeatType.Yearly), dateRemainingArgs);

                errorString = $"Failed to extract a date/time from the start of \"{argsString}\":\n" +
                    $"Not a valid repeating date + time because: {dateError}\n" +
                    $"    Valid example: \"every year 1 jan 12:00 UTC+0\"";
                return null;
            }

            // no disambiguation word, so we have to try every *repeatable* format, i.e.
            // no timestamps and AbsoluteDateTime are replaced by DayMonthTime
            var subArgsString = string.Join("", argsString.Skip(args.Indices[0]));
            if (TryParseInterval(subArgsString, out var intervalError) is var (intervalDto, intervalRemainingArgs))
                return (new ParseDateTimeResult(intervalDto, ScheduledJobRepeatType.Relative), intervalRemainingArgs);
            if (TryParseTimeInput(subArgsString, out var timeError2) is var (timeDto2, timeRemainingArgs2))
                return (new ParseDateTimeResult(timeDto2, ScheduledJobRepeatType.Daily), timeRemainingArgs2);
            if (TryParseWeekdayTime(subArgsString, out var weekdayError2) is var (weekdayDto2, weekdayRemainingArgs2))
                return (new ParseDateTimeResult(weekdayDto2, ScheduledJobRepeatType.Weekly), weekdayRemainingArgs2);
            if (TryParseDayMonthTime(subArgsString, out var dateError2) is var (dateDto2, dateRemainingArgs2))
                return (new ParseDateTimeResult(dateDto2, ScheduledJobRepeatType.Yearly), dateRemainingArgs2);

            if (args.Arguments.Length == 1)
            {
                errorString = "Failed to extract a date/time because there's nothing after the 'every'.\n" +
                              "    Valid example: \"every 1 hour\"";
                return null;
            }

            errorString = ErrorBasedOnIntendedFormat(args.Arguments.Skip(1).ToArray(),
                "Failed to extract a date/time from the start of \"{argsString}\" because it looks like a Discord timestamp, but repeating timestamps are not supported.\n" +
                $"    Valid examples: \"<t:1234567890>\", \"every 1 hour\"",
                $"Failed to extract a repeating date/time interval from the start of \"{argsString}\" because:\n" +
                $"    {intervalError}\n" +
                $"    Valid example: \"every 1 hour\"",
                $"Failed to extract a daily repeating time from the start of \"{argsString}\" because:\n" +
                $"    {timeError2}\n" +
                $"    Valid example: \"every day 12:00 UTC+0\"",
                $"Failed to extract a weekly repeating weekday + time from the start of \"{argsString}\" because:\n" +
                $"    {weekdayError2}\n" +
                $"    Valid example: \"every week monday 12:00 UTC+0\"",
                $"Failed to extract a yearly repeating date + time from the start of \"{argsString}\" because:\n" +
                $"    {dateError2}\n" +
                $"    Valid example: \"every year 1 jan 12:00 UTC+0\"",
                $"If you were trying for a different date/time format, see `.help remindme` for examples of all supported formats."
            );
            return null;
        }
        else
        {
            // no disambiguation word, so we have to try every valid format
            if (TryParseDiscordTimestamp(argsString, out var timestampError) is var (timestampDto, timestampRemainingArgs))
                return (new ParseDateTimeResult(timestampDto, ScheduledJobRepeatType.None), timestampRemainingArgs);
            if (TryParseInterval(argsString, out var intervalError) is var (intervalDto, intervalRemainingArgs))
                return (new ParseDateTimeResult(intervalDto, ScheduledJobRepeatType.None), intervalRemainingArgs);
            if (TryParseTimeInput(argsString, out var timeError) is var (timeDto, timeRemainingArgs))
                return (new ParseDateTimeResult(timeDto, ScheduledJobRepeatType.None), timeRemainingArgs);
            if (TryParseWeekdayTime(argsString, out var weekdayError) is var (weekdayDto, weekdayRemainingArgs))
                return (new ParseDateTimeResult(weekdayDto, ScheduledJobRepeatType.None), weekdayRemainingArgs);
            if (TryParseAbsoluteDateTime(argsString, out var dateError) is var (dateDto, dateRemainingArgs))
                return (new ParseDateTimeResult(dateDto, ScheduledJobRepeatType.None), dateRemainingArgs);

            errorString = ErrorBasedOnIntendedFormat(args.Arguments,
                timestampError ?? "<unreachable>",
                $"Failed to extract a date/time interval from the start of \"{argsString}\" because:\n" +
                $"    {intervalError}\n" +
                $"    Valid example: \"in 1 hour\"",
                $"Failed to extract a time from the start of \"{argsString}\" because:\n" +
                $"    {timeError}\n" +
                $"    Valid example: \"at 12:00 UTC+0\"",
                $"Failed to extract a weekday + time from the start of \"{argsString}\" because:\n" +
                $"    {weekdayError}\n" +
                $"    Valid example: \"on monday 12:00 UTC+0\"",
                $"Failed to extract a date + time from the start of \"{argsString}\" because:\n" +
                $"    {dateError}\n" +
                $"    Valid example: \"on 1 jan 2020 12:00 UTC+0\"",
                $"If you were trying for a different date/time format, see `.help remindme` for examples of all supported formats."
            );
            return null;
        }
    }

    private static string ErrorBasedOnIntendedFormat(
        string[] argTokens,
        string timestampError, string intervalError, string timeError, string weekdayError, string dateError,
        string footer)
    {
        // we assume the caller already checked there's at least one token
        if (argTokens[0].StartsWith('<'))
            return timestampError + "\n\n" + footer;

        if (TryParseTimeToken(argTokens[0], out _) is not null)
            return timeError + "\n\n" + footer;

        if (int.TryParse(argTokens[0], out _) || int.TryParse(argTokens[0].Substring(0, argTokens[0].Length - 2), out _))
        {
            if (argTokens.Length == 1)
                return timestampError + "\n\n" + footer;
            else if (argTokens.Length == 2)
                if (MonthNames.Keys.Contains(argTokens[1].ToLower()))
                    return dateError + "\n\n" + footer;
                else
                    return intervalError + "\n\n" + footer;
            else
                return dateError + "\n\n" + footer;
        }

        return weekdayError + "\n\n" + footer;
    }

    public static (DateTimeOffset, string)? TryParseDiscordTimestamp(string argsString, out string? errorString)
    {
        var args = DiscordHelper.GetArguments(argsString);
        if (!args.Arguments.Any())
        {
            errorString = $"empty or all-whitespace string \"{argsString}\" can't be a Discord timestamp";
            return null;
        }

        var match = Regex.Match(args.Arguments[0], "^<t:(?<epoch>[0-9]+)(:[a-z])?>$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var epochString = match.Groups["epoch"].Value;
            var epochSeconds = long.Parse(epochString);
            var dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);

            errorString = null;
            return (dateTimeOffset, string.Join("", argsString.Skip(args.Indices[0])));
        }
        else
        {
            errorString = $"\"{args.Arguments[0]}\" is not a Discord timestamp (e.g. \"<t:1234567890>\", \"<t:1234567890:R>\")";
            return null;
        }
    }

    public static (DateTimeOffset, string)? TryParseInterval(string argsString, out string? errorString, bool inThePast = false)
    {
        var args = DiscordHelper.GetArguments(argsString);
        if (!args.Arguments.Any())
        {
            errorString = $"empty or all-whitespace string \"{argsString}\" can't be a date/time interval";
            return null;
        }

        if (!int.TryParse(args.Arguments[0], out int dateInt))
        {
            errorString = $"\"{args.Arguments[0]}\" is not a positive integer";
            return null;
        }
        if (dateInt < 0)
        {
            errorString = $"{dateInt} is negative; only positive integers are supported";
            return null;
        }

        if (args.Arguments.Length < 2)
        {
            errorString = $"incomplete date/time interval: \"{argsString}\" contains a number but not a unit";
            return null;
        }

        var unitMatch = Regex.Match(args.Arguments[1], "^(?<unit>year|month|day|week|hour|minute|second)s?$", RegexOptions.IgnoreCase);
        if (!unitMatch.Success)
        {
            errorString = $"\"{args.Arguments[1]}\" is not one of the supported date/time interval units: year(s), month(s), day(s), week(s), hour(s), minute(s), second(s)";
            return null;
        }

        var unitString = unitMatch.Groups["unit"].Value;
        var dateTimeOffset = unitString switch
        {
            "year" => inThePast ? DateTimeHelper.UtcNow.AddYears(-dateInt) : DateTimeHelper.UtcNow.AddYears(dateInt),
            "month" => inThePast ? DateTimeHelper.UtcNow.AddMonths(-dateInt) : DateTimeHelper.UtcNow.AddMonths(dateInt),
            "week" => inThePast ? DateTimeHelper.UtcNow.AddDays(-(dateInt * 7)) : DateTimeHelper.UtcNow.AddDays(dateInt * 7),
            "day" => inThePast ? DateTimeHelper.UtcNow.AddDays(-dateInt) : DateTimeHelper.UtcNow.AddDays(dateInt),
            "hour" => inThePast ? DateTimeHelper.UtcNow.AddHours(-dateInt) : DateTimeHelper.UtcNow.AddHours(dateInt),
            "minute" => inThePast ? DateTimeHelper.UtcNow.AddMinutes(-dateInt) : DateTimeHelper.UtcNow.AddMinutes(dateInt),
            "second" => inThePast ? DateTimeHelper.UtcNow.AddSeconds(-dateInt) : DateTimeHelper.UtcNow.AddSeconds(dateInt),
            _ => throw new FormatException($"UNKNOWN_INERVAL_UNIT: {unitString}")
        };

        errorString = null;
        return (dateTimeOffset, string.Join("", argsString.Skip(args.Indices[1])));
    }

    public static (TimeSpan, string)? TryParseOffset(string argsString, out string? errorString)
    {
        var args = DiscordHelper.GetArguments(argsString);
        if (!args.Arguments.Any())
        {
            errorString = $"empty or all-whitespace string \"{argsString}\" can't be a time token";
            return null;
        }

        var offsetRegex = new Regex("^UTC(?<sign>\\+|-)(?<hours>\\d\\d?)(\\:(?<minutes>\\d\\d))?$", RegexOptions.IgnoreCase);
        var match = offsetRegex.Match(args.Arguments[0]);
        if (!match.Success)
        {
            errorString = $"\"{args.Arguments[0]}\" is not a valid UTC offset (e.g. \"UTC+0\", \"UTC-8\", \"UTC+11\", \"UTC-05:30\")";
            return null;
        }

        var sign = match.Groups["sign"].Value;
        var hours = match.Groups["hours"].Value;
        var minutes = match.Groups["minutes"].Value;

        var span = new TimeSpan(
            hours == "" ? 0 : int.Parse(hours),
            minutes == "" ? 0 : int.Parse(minutes),
            0);

        if (sign == "-")
            span = TimeSpan.Zero - span;

        errorString = null;
        return (span, string.Join("", argsString.Skip(args.Indices[0])));
    }

    // this is for a single "2pm" or "17:30" token in a larger date format
    // is called by TryParseTimeInput()
    public static (int, int, string)? TryParseTimeToken(string argsString, out string? errorString)
    {
        var args = DiscordHelper.GetArguments(argsString);
        if (!args.Arguments.Any())
        {
            errorString = $"empty or all-whitespace string \"{argsString}\" can't be a time token";
            return null;
        }

        var timeRegex = new Regex("^(?<hour>\\d\\d|\\d)(:(?<minute>\\d\\d))?(?<period>am|pm)?$", RegexOptions.IgnoreCase);
        var match = timeRegex.Match(args.Arguments[0]);
        if (!match.Success)
        {
            errorString = $"\"{args.Arguments[0]}\" is not a valid time (e.g. \"2pm\", \"2:30am\", \"17:15\", \"10:00pm\")";
            return null;
        }

        var hourInt = int.Parse(match.Groups["hour"].Value);

        var period = match.Groups["period"].Value;
        if (period.ToLower() == "pm" && (hourInt >= 1 && hourInt <= 11))
            hourInt += 12;
        else if (period.ToLower() == "am" && hourInt == 12)
            hourInt = 0;

        var minuteInt = 0;
        var minuteString = match.Groups["minute"].Value;
        if (minuteString != "")
            minuteInt = int.Parse(minuteString.ToLower());

        if (period == "" && minuteString == "")
        {
            errorString = $"\"{args.Arguments[0]}\" is just a number, not a valid time (e.g. \"2pm\", \"2:30am\", \"17:15\", \"10:00pm\")";
            return null;
        }

        errorString = null;
        return (hourInt, minuteInt, string.Join("", argsString.Skip(args.Indices[0])));
    }

    // this is for the date/time input format that only specifies a time, e.g. "2pm UTC+0"
    // calls TryParseTimeToken()
    public static (DateTimeOffset, string)? TryParseTimeInput(string argsString, out string? errorString)
    {
        if (TryParseTimeToken(argsString, out errorString) is not var (hours, minutes, argsAfterTime))
            return null;

        if (argsAfterTime.Trim() == "")
        {
            errorString = $"\"{argsString}\" is missing a UTC offset after the time (e.g. \"UTC+0\", \"UTC-8\", \"UTC+11\", \"UTC-05:30\")";
            return null;
        }

        if (TryParseOffset(argsAfterTime, out errorString) is not var (offset, argsAfterOffset))
            return null;

        var dto = new DateTimeOffset(
            DateTimeHelper.UtcNow.Year, DateTimeHelper.UtcNow.Month, DateTimeHelper.UtcNow.Day,
            hours, minutes, 0, offset);

        if (dto < DateTimeHelper.UtcNow)
            dto = dto.AddDays(1);

        errorString = null;
        return (dto, argsAfterOffset);
    }

    private static Dictionary<string, int> WeekdayNames = new() {
        { "sunday", 0 },
        { "monday", 1 },
        { "tuesday", 2 },
        { "wednesday", 3 },
        { "thursday", 4 },
        { "friday", 5 },
        { "saturday", 6 },
        { "sun", 0 },
        { "mon", 1 },
        { "tue", 2 },
        { "wed", 3 },
        { "thu", 4 },
        { "fri", 5 },
        { "sat", 6 },
    };

    public static (DateTimeOffset, string)? TryParseWeekdayTime(string argsString, out string? errorString)
    {
        var args = DiscordHelper.GetArguments(argsString);
        if (!args.Arguments.Any())
        {
            errorString = $"empty or all-whitespace string \"{argsString}\" can't be a weekday + time";
            return null;
        }

        var weekdayToken = args.Arguments[0].ToLower();
        if (!WeekdayNames.Keys.Contains(weekdayToken))
        {
            errorString = $"\"{weekdayToken}\" is not one of the supported weekday names: sun(day), mon(day), tue(sday), wed(nesday), thu(rsday), fri(day), sat(urday)";
            return null;
        }
        var weekdayInt = WeekdayNames[weekdayToken];

        var argsAfterWeekday = string.Join("", argsString.Skip(args.Indices[0]));
        if (argsAfterWeekday.Trim() == "")
        {
            errorString = $"\"{argsString}\" is missing a time and UTC offset after the weekday";
            return null;
        }

        if (TryParseTimeToken(argsAfterWeekday, out errorString) is not var (hours, minutes, argsAfterTime))
            return null;

        if (argsAfterTime.Trim() == "")
        {
            errorString = $"\"{argsString}\" is missing a UTC offset after the time (e.g. \"UTC+0\", \"UTC-8\", \"UTC+11\", \"UTC-05:30\")";
            return null;
        }

        if (TryParseOffset(argsAfterTime, out errorString) is not var (offset, argsAfterOffset))
            return null;

        var dto = new DateTimeOffset(
            DateTimeHelper.UtcNow.Year, DateTimeHelper.UtcNow.Month, DateTimeHelper.UtcNow.Day,
            hours, minutes, 0, offset);

        // monday -> friday = 4
        // friday -> monday = -4 (-4+7 = 3)
        var daysToAdd = weekdayInt - (int)DateTimeHelper.UtcNow.DayOfWeek;
        if (daysToAdd <= 0)
            daysToAdd += 7;
        dto = dto.AddDays(daysToAdd);

        errorString = null;
        return (dto, argsAfterOffset);
    }

    public static int? TryParseDateToken(string dateToken, out string? errorString)
    {
        int dateInt;
        bool isInt = int.TryParse(dateToken, out dateInt);

        // support "st"/"nd"/"rd"/"th" suffixes without advertising them
        bool isIntWithSuffix = false;
        if (!isInt)
            isIntWithSuffix = dateToken.Length >= 2 && int.TryParse(dateToken.Substring(0, dateToken.Length - 2), out dateInt);

        if (!isInt && !isIntWithSuffix) {
            errorString = $"\"{dateToken}\" is not a positive integer";
            return null;
        }
        if (dateInt <= 0)
        {
            errorString = $"{dateToken} is zero or negative; days are always positive";
            return null;
        }
        if (dateInt > 31)
        {
            errorString = $"{dateToken} is not a valid day because days never go higher than 31";
            return null;
        }

        errorString = null;
        return dateInt;
    }

    private static Dictionary<string, int> MonthNames = new() {
        { "january", 1 },
        { "jan", 1 },
        { "february", 2 },
        { "feb", 2 },
        { "march", 3 },
        { "mar", 3 },
        { "april", 4 },
        { "apr", 4 },
        { "may", 5 },
        { "june", 6 },
        { "jun", 6 },
        { "july", 7 },
        { "jul", 7 },
        { "august", 8 },
        { "aug", 8 },
        { "september", 9 },
        { "sep", 9 },
        { "october", 10 },
        { "oct", 10 },
        { "november", 11 },
        { "nov", 11 },
        { "december", 12 },
        { "dec", 12 },
    };

    public static int? TryParseMonthToken(string inputMonthToken, out string? errorString)
    {
        var monthToken = inputMonthToken.ToLower();
        if (!MonthNames.Keys.Contains(monthToken))
        {
            errorString = $"\"{monthToken}\" is not one of the supported month names: jan(uary), feb(ruary), mar(ch), apr(il), may, jun(e), jul(y), aug(ust), sep(tember), oct(ober), nov(ember), dec(ember)";
            return null;
        }

        errorString = null;
        return MonthNames[monthToken];
    }

    // same as TryParseAbsoluteDateTime, but without the year part
    public static (DateTimeOffset, string)? TryParseDayMonthTime(string argsString, out string? errorString)
    {
        var args = DiscordHelper.GetArguments(argsString);
        if (!args.Arguments.Any())
        {
            errorString = $"empty or all-whitespace string \"{argsString}\" can't be a date + time";
            return null;
        }

        if (TryParseDateToken(args.Arguments[0], out errorString) is not int dateInt)
            return null;

        if (args.Arguments.Length < 2)
        {
            errorString = $"\"{argsString}\" is missing a month, time and UTC offset after the day";
            return null;
        }

        if (TryParseMonthToken(args.Arguments[1], out errorString) is not int monthInt)
            return null;

        var argsAfterMonth = string.Join("", argsString.Skip(args.Indices[1]));
        if (argsAfterMonth.Trim() == "")
        {
            errorString = $"\"{argsString}\" is missing a time and UTC offset after the month";
            return null;
        }

        if (TryParseTimeToken(argsAfterMonth, out errorString) is not var (hours, minutes, argsAfterTime))
            return null;

        if (argsAfterTime.Trim() == "")
        {
            errorString = $"\"{argsString}\" is missing a UTC offset after the time (e.g. \"UTC+0\", \"UTC-8\", \"UTC+11\", \"UTC-05:30\")";
            return null;
        }

        if (TryParseOffset(argsAfterTime, out errorString) is not var (offset, argsAfterOffset))
            return null;

        var dto = new DateTimeOffset(DateTimeHelper.UtcNow.Year, monthInt, dateInt, hours, minutes, 0, offset);

        if (dto < DateTimeHelper.UtcNow)
            dto = dto.AddYears(1);

        errorString = null;
        return (dto, argsAfterOffset);
    }

    public static (DateTimeOffset, string)? TryParseAbsoluteDateTime(string argsString, out string? errorString)
    {
        var args = DiscordHelper.GetArguments(argsString);
        if (!args.Arguments.Any())
        {
            errorString = $"empty or all-whitespace string \"{argsString}\" can't be a date + time";
            return null;
        }

        if (TryParseDateToken(args.Arguments[0], out errorString) is not int dateInt)
            return null;

        if (args.Arguments.Length < 2)
        {
            errorString = $"\"{argsString}\" is missing a month, year, time and UTC offset after the day";
            return null;
        }

        if (TryParseMonthToken(args.Arguments[1], out errorString) is not int monthInt)
            return null;

        if (args.Arguments.Length < 3)
        {
            errorString = $"\"{argsString}\" is missing a year, time and UTC offset after the month";
            return null;
        }

        var yearArg = args.Arguments[2];
        if (!int.TryParse(yearArg, out int yearInt))
        {
            errorString = $"\"{yearArg}\" is not a positive integer";
            return null;
        }
        if (yearInt < 0)
        {
            errorString = $"{yearInt} is negative; only positive years are supported";
            return null;
        }

        var argsAfterYear = string.Join("", argsString.Skip(args.Indices[2]));
        if (argsAfterYear.Trim() == "")
        {
            errorString = $"\"{argsString}\" is missing a time and UTC offset after the year";
            return null;
        }

        if (TryParseTimeToken(argsAfterYear, out errorString) is not var (hours, minutes, argsAfterTime))
            return null;

        if (argsAfterTime.Trim() == "")
        {
            errorString = $"\"{argsString}\" is missing a UTC offset after the time (e.g. \"UTC+0\", \"UTC-8\", \"UTC+11\", \"UTC-05:30\")";
            return null;
        }

        if (TryParseOffset(argsAfterTime, out errorString) is not var (offset, argsAfterOffset))
            return null;

        var dto = new DateTimeOffset(yearInt, monthInt, dateInt, hours, minutes, 0, offset);

        errorString = null;
        return (dto, argsAfterOffset);
    }
}

public class ParseDateTimeResult
{
    public ScheduledJobRepeatType RepeatType;
    public DateTimeOffset Time;

    public ParseDateTimeResult(DateTimeOffset time, ScheduledJobRepeatType repeatType)
    {
        Time = time;
        RepeatType = repeatType;
    }
}