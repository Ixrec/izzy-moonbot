using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Izzy_Moonbot.Helpers;
using Izzy_Moonbot.Settings;

namespace Izzy_Moonbot.Service;

public class QuoteService
{
    private readonly QuoteStorage _quoteStorage;
    private readonly Dictionary<ulong, User> _users;
    
    public QuoteService(QuoteStorage quoteStorage, Dictionary<ulong, User> users)
    {
        _quoteStorage = quoteStorage;
        _users = users;
    }

    /// <summary>
    /// Check whether an alias exists or not.
    /// </summary>
    /// <param name="alias">The alias to check.</param>
    /// <returns>Whether the alias exists or not.</returns>
    public bool AliasExists(string alias)
    {
        return _quoteStorage.Aliases.ContainsKey(alias);
    }

    /// <summary>
    /// Work out what the alias refers to.
    /// </summary>
    /// <param name="alias">The alias to check.</param>
    /// <param name="guild">The guild to check for the user in.</param>
    /// <returns>"user" if the alias refers to a user, "category" if not.</returns>
    /// <exception cref="NullReferenceException">If the alias doesn't exist.</exception>
    public string AliasRefersTo(string alias, SocketGuild guild)
    {
        if (_quoteStorage.Aliases.ContainsKey(alias))
        {
            var value = _quoteStorage.Aliases[alias];

            if (ulong.TryParse(value, out var id))
            {
                var potentialUser = guild.GetUser(id);
                if (potentialUser == null) return "category";

                return "user";
            }

            return "category";
        }

        throw new NullReferenceException("That alias does not exist.");
    }

    /// <summary>
    /// Process an alias into a IUser.
    /// </summary>
    /// <param name="alias">The alias to process.</param>
    /// <param name="guild">The guild to get the user from.</param>
    /// <returns>An instance of IUser that this alias refers to.</returns>
    /// <exception cref="TargetException">If the user couldn't be found (left the server).</exception>
    /// <exception cref="ArgumentException">If the alias doesn't refer to a user.</exception>
    /// <exception cref="NullReferenceException">If the alias doesn't exist.</exception>
    public IUser ProcessAlias(string alias, SocketGuild guild)
    {
        if (_quoteStorage.Aliases.ContainsKey(alias))
        {
            var value = _quoteStorage.Aliases[alias];

            if (ulong.TryParse(value, out var id))
            {
                var potentialUser = guild.GetUser(id);
                if (potentialUser == null)
                    throw new TargetException("The user this alias referenced to cannot be found.");

                return potentialUser;
            }
            throw new ArgumentException("This alias cannot be converted to an IUser.");
        }

        throw new NullReferenceException("That alias does not exist.");
    }
    
    /// <summary>
    /// Process an alias into a category name.
    /// </summary>
    /// <param name="alias">The alias to process.</param>
    /// <returns>The category name this alias refers to.</returns>
    /// <exception cref="NullReferenceException">If the alias doesn't exist.</exception>
    public string ProcessAlias(string alias)
    {
        if (_quoteStorage.Aliases.ContainsKey(alias))
        {
            var value = _quoteStorage.Aliases[alias];

            return value;
        }

        throw new NullReferenceException("That alias does not exist.");
    }
    
    /// <summary>
    /// Check if a user has quotes.
    /// </summary>
    /// <param name="user">The user to check.</param>
    /// <returns>Whether the user has quotes or not.</returns>
    public bool HasQuotes(IUser user)
    {
        return _quoteStorage.Quotes.ContainsKey(user.Id.ToString());
    }

    /// <summary>
    /// Check if a category exists.
    /// </summary>
    /// <param name="name">The category name to check.</param>
    /// <returns>Whether the category exists or not.</returns>
    public bool CategoryExists(string name)
    {
        return _quoteStorage.Quotes.ContainsKey(name);
    }
    
    /// <summary>
    /// Get a quote by a valid Discord user and a quote id.
    /// </summary>
    /// <param name="user">The user to get the quote of.</param>
    /// <param name="id">The quote id to get.</param>
    /// <returns>A Quote containing the quote information.</returns>
    /// <exception cref="NullReferenceException">If the user doesn't have any quotes.</exception>
    /// <exception cref="IndexOutOfRangeException">If the id provided is larger than the amount of quotes the user has.</exception>
    public Quote GetQuote(IUser user, int id)
    {
        if (!_quoteStorage.Quotes.ContainsKey(user.Id.ToString()))
            throw new NullReferenceException("That user does not have any quotes.");
        
        var quotes = _quoteStorage.Quotes[user.Id.ToString()];

        if (quotes.Count <= id) throw new IndexOutOfRangeException("That quote ID does not exist.");

        var quoteName = user.Username;
        if (user is IGuildUser guildUser) quoteName = guildUser.DisplayName;
        var quoteContent = quotes[id];

        return new Quote(id, quoteName, quoteContent);
    }
    
    /// <summary>
    /// Get a quote in a category by a quote id.
    /// </summary>
    /// <param name="name">The category name to get the quote of.</param>
    /// <param name="id">The quote id to get.</param>
    /// <returns>A Quote containing the quote information.</returns>
    /// <exception cref="NullReferenceException">If the category doesn't have any quotes.</exception>
    /// <exception cref="IndexOutOfRangeException">If the id provided is larger than the amount of quotes the category contains.</exception>
    public Quote GetQuote(string name, int id)
    {
        if (!_quoteStorage.Quotes.ContainsKey(name))
            throw new NullReferenceException("That category does not have any quotes.");
        
        var quotes = _quoteStorage.Quotes[name];

        if (quotes.Count <= id) throw new IndexOutOfRangeException("That quote ID does not exist.");
        
        var quoteContent = quotes[id];

        return new Quote(id, name, quoteContent);
    }

    /// <summary>
    /// Get a list of quotes from a valid Discord user.
    /// </summary>
    /// <param name="user">The user to get the quotes of.</param>
    /// <returns>An array of Quotes that this user has.</returns>
    /// <exception cref="NullReferenceException">If the user doesn't have any quotes.</exception>
    public Quote[] GetQuotes(IUser user)
    {
        if (!_quoteStorage.Quotes.ContainsKey(user.Id.ToString()))
            throw new NullReferenceException("That user does not have any quotes.");
        
        var quotes = _quoteStorage.Quotes[user.Id.ToString()].Select(quoteContent =>
        {
            var quoteName = user.Username;
            if (user is IGuildUser guildUser) quoteName = guildUser.DisplayName;

            return new Quote(_quoteStorage.Quotes[user.Id.ToString()].IndexOf(quoteContent), quoteName, quoteContent);
        }).ToArray();

        return quotes;
    }
    
    /// <summary>
    /// Get a list of quotes from a category.
    /// </summary>
    /// <param name="name">The category name to get the quotes of.</param>
    /// <returns>An array of Quotes that this category contains.</returns>
    /// <exception cref="NullReferenceException">If the category doesn't have any quotes.</exception>
    public Quote[] GetQuotes(string name)
    {
        if (!_quoteStorage.Quotes.ContainsKey(name))
            throw new NullReferenceException("That category does not have any quotes.");
        
        var quotes = _quoteStorage.Quotes[name].Select(quoteContent =>
        {
            return new Quote(_quoteStorage.Quotes[name].IndexOf(quoteContent), name, quoteContent);
        }).ToArray();

        return quotes;
    }

    /// <summary>
    /// Gets a random quote from a random user/category.
    /// </summary>
    /// <param name="guild">The guild to get the user from, for name fetching purposes.</param>
    /// <returns>A Quote containing the quote information.</returns>
    public Quote GetRandomQuote(SocketGuild guild)
    {
        Random rnd = new Random();
        var key = _quoteStorage.Quotes.Keys.ToArray()[rnd.Next(_quoteStorage.Quotes.Keys.Count)];

        var quotes = _quoteStorage.Quotes[key];

        var isUser = ulong.TryParse(key, out var id);
        var quoteName = key;

        if (isUser)
        {
            var potentialUser = guild.GetUser(id);
            if (potentialUser == null)
            {
                quoteName = _users.ContainsKey(id) ? _users[id].Username : $"<@{id}>";
            }
            else quoteName = potentialUser.DisplayName;
        }

        var quoteId = rnd.Next(quotes.Count);
        var quoteContent = quotes[quoteId];

        return new Quote(quoteId, quoteName, quoteContent);
    }
    
    /// <summary>
    /// Get a random quote by a valid Discord user.
    /// </summary>
    /// <param name="user">The user to get the quote of.</param>
    /// <returns>A Quote containing the quote information.</returns>
    /// <exception cref="NullReferenceException">If the user doesn't have any quotes.</exception>
    public Quote GetRandomQuote(IUser user)
    {
        if (!_quoteStorage.Quotes.ContainsKey(user.Id.ToString()))
            throw new NullReferenceException("That user does not have any quotes.");
        
        var quotes = _quoteStorage.Quotes[user.Id.ToString()];
        
        var quoteName = user.Username;
        if (user is IGuildUser guildUser) quoteName = guildUser.DisplayName;
        
        Random rnd = new Random();
        var quoteId = rnd.Next(quotes.Count);
        var quoteContent = quotes[quoteId];

        return new Quote(quoteId, quoteName, quoteContent);
    }
    
    /// <summary>
    /// Get a random quote in a category.
    /// </summary>
    /// <param name="name">The category name to get the quote of.</param>
    /// <returns>A Quote containing the quote information.</returns>
    /// <exception cref="NullReferenceException">If the category doesn't have any quotes.</exception>
    public Quote GetRandomQuote(string name)
    {
        if (!_quoteStorage.Quotes.ContainsKey(name))
            throw new NullReferenceException("That category does not have any quotes.");
        
        var quotes = _quoteStorage.Quotes[name];

        Random rnd = new Random();
        var quoteId = rnd.Next(quotes.Count);
        var quoteContent = quotes[quoteId];

        return new Quote(quoteId, name, quoteContent);
    }

    /// <summary>
    /// Add a quote to a user.
    /// </summary>
    /// <param name="user">The user to add the quote to.</param>
    /// <param name="content">The content of the quote.</param>
    /// <returns>The newly created Quote.</returns>
    public async Task<Quote> AddQuote(IUser user, string content)
    {
        if (!_quoteStorage.Quotes.ContainsKey(user.Id.ToString()))
            _quoteStorage.Quotes.Add(user.Id.ToString(), new List<string>());

        _quoteStorage.Quotes[user.Id.ToString()].Add(content);

        await FileHelper.SaveQuoteStorageAsync(_quoteStorage);

        var quoteId = _quoteStorage.Quotes[user.Id.ToString()].Count - 1;
        var quoteName = user.Username;
        if (user is IGuildUser guildUser) quoteName = guildUser.DisplayName;

        return new Quote(quoteId, quoteName, content);
    }
    
    /// <summary>
    /// Add a quote to a category.
    /// </summary>
    /// <param name="name">The category name to add the quote to.</param>
    /// <param name="content">The content of the quote.</param>
    /// <returns>The newly created Quote.</returns>
    public async Task<Quote> AddQuote(string name, string content)
    {
        if (!_quoteStorage.Quotes.ContainsKey(name))
            _quoteStorage.Quotes.Add(name, new List<string>());

        _quoteStorage.Quotes[name].Add(content);
        
        await FileHelper.SaveQuoteStorageAsync(_quoteStorage);

        var quoteId = _quoteStorage.Quotes[name].Count - 1;

        return new Quote(quoteId, name, content);
    }
    
    /// <summary>
    /// Remove a quote from a user.
    /// </summary>
    /// <param name="user">The user to remove the quote from.</param>
    /// <param name="id">The id of the quote to remove.</param>
    /// <returns>The Quote that was removed.</returns>
    /// <exception cref="NullReferenceException">If the user doesn't have any quotes.</exception>
    /// <exception cref="IndexOutOfRangeException">If the quote id provided doesn't exist.</exception>
    public async Task<Quote> RemoveQuote(IUser user, int id)
    {
        if (!_quoteStorage.Quotes.ContainsKey(user.Id.ToString()))
            throw new NullReferenceException("That user does not have any quotes.");
        
        var quotes = _quoteStorage.Quotes[user.Id.ToString()];

        if (quotes.Count <= id) throw new IndexOutOfRangeException("That quote ID does not exist.");
        
        var quoteName = user.Username;
        if (user is IGuildUser guildUser) quoteName = guildUser.DisplayName;

        var quoteContent = quotes[id];
        
        _quoteStorage.Quotes[user.Id.ToString()].RemoveAt(id);

        if (_quoteStorage.Quotes[user.Id.ToString()].Count == 0)
            _quoteStorage.Quotes.Remove(user.Id.ToString());
        
        await FileHelper.SaveQuoteStorageAsync(_quoteStorage);
        
        return new Quote(id, quoteName, quoteContent);
    }
    
    /// <summary>
    /// Remove a quote from a category.
    /// </summary>
    /// <param name="name">The category name to remove the quote from.</param>
    /// <param name="id">The id of the quote to remove.</param>
    /// <returns>The Quote that was removed.</returns>
    /// <exception cref="NullReferenceException">If the category doesn't have any quotes.</exception>
    /// <exception cref="IndexOutOfRangeException">If the quote id provided doesn't exist.</exception>
    public async Task<Quote> RemoveQuote(string name, int id)
    {
        if (!_quoteStorage.Quotes.ContainsKey(name))
            throw new NullReferenceException("That category does not have any quotes.");
        
        var quotes = _quoteStorage.Quotes[name];

        if (quotes.Count <= id) throw new IndexOutOfRangeException("That quote ID does not exist.");

        var quoteContent = quotes[id];
        
        _quoteStorage.Quotes[name].RemoveAt(id);

        if (_quoteStorage.Quotes[name].Count == 0)
            _quoteStorage.Quotes.Remove(name);
        
        await FileHelper.SaveQuoteStorageAsync(_quoteStorage);
        
        return new Quote(id, name, quoteContent);
    }
    
    /// <summary>
    /// Remove all quote from a user.
    /// </summary>
    /// <param name="user">The user to remove the quotes from.</param>
    /// <exception cref="NullReferenceException">If the user doesn't have any quotes.</exception>
    public async Task RemoveQuotes(IUser user)
    {
        if (!_quoteStorage.Quotes.ContainsKey(user.Id.ToString()))
            throw new NullReferenceException("That user does not have any quotes.");
        
        _quoteStorage.Quotes.Remove(user.Id.ToString());
        
        await FileHelper.SaveQuoteStorageAsync(_quoteStorage);
    }
    
    /// <summary>
    /// Remove all quote from a category.
    /// </summary>
    /// <param name="name">The category name to remove the quotes from.</param>
    /// <exception cref="NullReferenceException">If the category doesn't have any quotes.</exception>
    public async Task RemoveQuotes(string name)
    {
        if (!_quoteStorage.Quotes.ContainsKey(name))
            throw new NullReferenceException("That category does not have any quotes.");
        
        _quoteStorage.Quotes.Remove(name);
        
        await FileHelper.SaveQuoteStorageAsync(_quoteStorage);
    }
}

public class Quote
{
    public int Id;
    public string Name;
    public string Content;

    public Quote(int id, string name, string content)
    {
        Id = id;
        Name = name;
        Content = content;
    }
}