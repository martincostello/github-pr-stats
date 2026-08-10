using System.Globalization;
using System.Text.Json.Nodes;
using Octokit;

namespace GitHubPRStats;

internal sealed class Cache
{
    private const string IndexFileName = "pulls-index.json";
    private const string LegacyCursorFileName = "cursor";
    private const string PublishedFileName = "published.json";
    private const string ReposFileName = "repos.json";
    private const string StateFileName = "state.json";

    /// <summary>
    /// The maximum number of results the GitHub search API will return for a single query.
    /// </summary>
    private const int SearchResultLimit = 1_000;

    private readonly GitHubClient _github;
    private readonly TokenCredentialStore _credentials;
    private readonly string _path;
    private User? _user;

    public Cache(string path, string accessToken)
    {
        _credentials = new TokenCredentialStore(accessToken);
        _github = new GitHubClient(new ProductHeaderValue("GitHubPRStats", "1.0.0"), _credentials);
        _path = path;
    }

    /// <summary>
    /// Incrementally hydrates the cache from GitHub.
    /// </summary>
    /// <remarks>
    /// The first run walks backwards a month at a time from today to the date the account was
    /// created. Subsequent runs only fetch pull requests that have been created or updated since
    /// the previous run, so the tool is cheap to re-run periodically.
    /// </remarks>
    public async Task BuildAsync()
    {
        Directory.CreateDirectory(_path);

        var me = await CurrentUserAsync();
        var state = await GetStateAsync();
        var pulls = (await GetPullsAsync()).ToDictionary((p) => p.Key);

        var now = DateTimeOffset.UtcNow;
        int changed = 0;

        if (state.LastSyncedAt is { } lastSyncedAt)
        {
            // Overlap the window by a day so that nothing is missed if the previous run
            // raced with a pull request being opened, merged or closed as it finished.
            var since = lastSyncedAt.AddDays(-1);

            Console.WriteLine($"Searching for pull requests updated since {since:u}...");

            changed += await SynchronizeDataAsync(pulls, me, since, now, SearchBy.Updated);
        }

        var oldest = state.OldestCreated ?? now;

        if (!state.BackfillComplete)
        {
            while (oldest > me.CreatedAt)
            {
                var from = oldest.AddMonths(-1);

                Console.WriteLine($"Searching for pull requests created between {from:u} and {oldest:u}...");

                changed += await SynchronizeDataAsync(pulls, me, from, oldest, SearchBy.Created);

                oldest = from;

                // Checkpoint after each window so an interrupted backfill resumes where it left off.
                await SavePullsAsync(pulls.Values);
                await SaveStateAsync(state = state with { OldestCreated = oldest });
            }

            state = state with { BackfillComplete = true };
        }

        await SavePullsAsync(pulls.Values);
        await SaveStateAsync(state with { LastSyncedAt = now });

        Console.WriteLine($"Cached {pulls.Count:N0} pull requests ({changed:N0} added or updated).");

        await BuildReposAsync(pulls.Values);
    }

    public async Task<User> CurrentUserAsync()
        => _user ??= await ExecuteAsync(_github.User.Current);

    public async Task<IReadOnlyList<Pull>> GetPullsAsync()
    {
        // A cache written by an earlier version of the tool can contain the same pull request
        // more than once, so key them and let the most recently written entry win.
        var result = new Dictionary<string, Pull>(StringComparer.Ordinal);

        await foreach (var json in ReadLinesAsync(IndexFileName))
        {
            var pull = new Pull(
                json["owner"]!.GetValue<string>(),
                json["repo"]!.GetValue<string>(),
                json["number"]!.GetValue<int>(),
                json["created"]!.GetValue<DateTimeOffset>(),
                json["state"]!.GetValue<string>(),
                json["merged"]!.GetValue<bool>(),
                json["url"]!.GetValue<string>());

            result[pull.Key] = pull;
        }

        return [.. result.Values];
    }

    public async Task<IReadOnlyList<Repo>> GetReposAsync()
    {
        var result = new Dictionary<string, Repo>(StringComparer.OrdinalIgnoreCase);

        await foreach (var json in ReadLinesAsync(ReposFileName))
        {
            var repo = new Repo(
                json["owner"]!.GetValue<string>(),
                json["name"]!.GetValue<string>(),
                json["language"]?.GetValue<string>() ?? "Unknown",
                json["url"]!.GetValue<string>());

            result[repo.Key] = repo;
        }

        return [.. result.Values];
    }

    /// <summary>
    /// Gets the keys of the pull requests that have already been published to Grafana.
    /// </summary>
    public async Task<HashSet<string>> GetPublishedAsync()
    {
        var path = Path.Join(_path, PublishedFileName);

        if (!File.Exists(path))
        {
            return new(StringComparer.Ordinal);
        }

        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();

        return new(json.Select((p) => p!.GetValue<string>()), StringComparer.Ordinal);
    }

    public async Task SavePublishedAsync(IEnumerable<string> keys)
    {
        var json = new JsonArray();

        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            json.Add(key);
        }

        await WriteAsync(PublishedFileName, json.ToJsonString());
    }

    private async Task BuildReposAsync(IEnumerable<Pull> pulls)
    {
        var repos = (await GetReposAsync()).ToDictionary((p) => $"{p.Owner}/{p.Name}", StringComparer.OrdinalIgnoreCase);

        var missing = pulls.Select((p) => (p.Owner, p.Repo))
                           .Distinct()
                           .Where((p) => !repos.ContainsKey($"{p.Owner}/{p.Repo}"))
                           .ToList();

        if (missing.Count is 0)
        {
            Console.WriteLine($"All {repos.Count:N0} repositories are already cached.");
            return;
        }

        Console.WriteLine($"Caching {missing.Count:N0} repositories...");

        foreach (var (owner, name) in missing)
        {
            var repo = await GetRepositoryAsync(owner, name);

            repos[$"{owner}/{name}"] = repo is null
                ? new(owner, name, "Unknown", $"https://github.com/{owner}/{name}")
                : new(repo.Owner.Login, repo.Name, repo.Language ?? "Unknown", repo.HtmlUrl);
        }

        await SaveReposAsync(repos.Values);
    }

    private async Task<Repository?> GetRepositoryAsync(string owner, string name)
    {
        try
        {
            try
            {
                return await ExecuteAsync(() => _github.Repository.Get(owner, name));
            }
            catch (ForbiddenException)
            {
                // Repo only allows fine-grained access tokens
                _credentials.Anonymous = true;
                return await ExecuteAsync(() => _github.Repository.Get(owner, name));
            }
            finally
            {
                _credentials.Anonymous = false;
            }
        }
        catch (NotFoundException)
        {
            // The repository has since been deleted or made private
            Console.WriteLine($"Repository {owner}/{name} is no longer accessible.");
            return null;
        }
    }

    /// <summary>
    /// Searches for the current user's pull requests within the specified window and merges
    /// them into <paramref name="pulls"/>, returning how many entries were added or changed.
    /// </summary>
    private async Task<int> SynchronizeDataAsync(
        Dictionary<string, Pull> pulls,
        User me,
        DateTimeOffset from,
        DateTimeOffset to,
        SearchBy searchBy)
    {
        var query = new SearchIssuesRequest()
        {
            Author = me.Login,
            Is = [IssueIsQualifier.PullRequest],
            Order = SortDirection.Descending,
            Page = 1,
            PerPage = 100,
            SortField = IssueSearchSort.Created,
            Type = IssueTypeQualifier.PullRequest,
        };

        if (searchBy is SearchBy.Updated)
        {
            query.Updated = new DateRange(from, to);
        }
        else
        {
            query.Created = new DateRange(from, to);
        }

        int changed = 0;

        while (true)
        {
            var results = await ExecuteAsync(() => _github.Search.SearchIssues(query));

            // The search API never returns more than the first 1,000 matches, so
            // halve the window and try again if the results would be truncated.
            if (query.Page is 1 && results.TotalCount > SearchResultLimit && (to - from) > TimeSpan.FromDays(1))
            {
                var midpoint = from + ((to - from) / 2);

                return await SynchronizeDataAsync(pulls, me, from, midpoint, searchBy) +
                       await SynchronizeDataAsync(pulls, me, midpoint, to, searchBy);
            }

            foreach (var issue in results.Items)
            {
                if (issue.PullRequest is not { } metadata)
                {
                    continue;
                }

                // E.g. https://github.com/dotnet/aspnetcore/pull/56395
                var segments = issue.HtmlUrl.Split('/');
                var owner = segments[3];
                var repo = segments[4];
                var number = int.Parse(segments[6], CultureInfo.InvariantCulture);

                if (owner == me.Login)
                {
                    continue;
                }

                var pull = new Pull(
                    owner,
                    repo,
                    number,
                    issue.CreatedAt,
                    issue.State.ToString(),
                    metadata.Merged,
                    metadata.HtmlUrl ?? issue.HtmlUrl);

                if (!pulls.TryGetValue(pull.Key, out var existing) || existing != pull)
                {
                    pulls[pull.Key] = pull;
                    changed++;
                }
            }

            if (results.Items.Count < query.PerPage)
            {
                break;
            }

            query.Page++;
        }

        return changed;
    }

    private async Task<SyncState> GetStateAsync()
    {
        var path = Path.Join(_path, StateFileName);

        if (File.Exists(path))
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;

            return new(
                json["oldestCreated"]?.GetValue<DateTimeOffset>(),
                json["lastSyncedAt"]?.GetValue<DateTimeOffset>(),
                json["backfillComplete"]?.GetValue<bool>() ?? false);
        }

        // Resume from where a cache built by an earlier version of the tool got to.
        var cursor = Path.Join(_path, LegacyCursorFileName);

        if (File.Exists(cursor))
        {
            var created = DateTimeOffset.Parse(await File.ReadAllTextAsync(cursor), CultureInfo.InvariantCulture);
            return new(created, null, false);
        }

        return new(null, null, false);
    }

    private async Task SaveStateAsync(SyncState state)
    {
        var json = new JsonObject()
        {
            ["oldestCreated"] = state.OldestCreated,
            ["lastSyncedAt"] = state.LastSyncedAt,
            ["backfillComplete"] = state.BackfillComplete,
        };

        await WriteAsync(StateFileName, json.ToJsonString());
    }

    private async Task SavePullsAsync(IEnumerable<Pull> pulls)
    {
        var lines = pulls.OrderByDescending((p) => p.Created)
                         .ThenBy((p) => p.Key, StringComparer.Ordinal)
                         .Select((p) => new JsonObject()
                         {
                             ["owner"] = p.Owner,
                             ["repo"] = p.Repo,
                             ["number"] = p.Number,
                             ["created"] = p.Created,
                             ["state"] = p.State,
                             ["merged"] = p.Merged,
                             ["url"] = p.Url,
                         }.ToJsonString());

        await WriteAsync(IndexFileName, string.Join(Environment.NewLine, lines));
    }

    private async Task SaveReposAsync(IEnumerable<Repo> repos)
    {
        var lines = repos.OrderBy((p) => $"{p.Owner}/{p.Name}", StringComparer.OrdinalIgnoreCase)
                         .Select((p) => new JsonObject()
                         {
                             ["owner"] = p.Owner,
                             ["name"] = p.Name,
                             ["language"] = p.Language,
                             ["url"] = p.Url,
                         }.ToJsonString());

        await WriteAsync(ReposFileName, string.Join(Environment.NewLine, lines));
    }

    private async IAsyncEnumerable<JsonNode> ReadLinesAsync(string fileName)
    {
        var path = Path.Join(_path, fileName);

        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return JsonNode.Parse(line)!;
            }
        }
    }

    /// <summary>
    /// Writes the file via a temporary file so that an interrupted write cannot corrupt the cache.
    /// </summary>
    private async Task WriteAsync(string fileName, string contents)
    {
        Directory.CreateDirectory(_path);

        var path = Path.Join(_path, fileName);
        var temporary = path + ".tmp";

        await File.WriteAllTextAsync(temporary, contents);

        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (RateLimitExceededException ex)
            {
                var delay = ex.GetRetryAfterTimeSpan().Add(TimeSpan.FromSeconds(2));
                Console.WriteLine($"Rate limit exceeded. Waiting for {delay}...");
                await Task.Delay(delay);
            }
            catch (SecondaryRateLimitExceededException)
            {
                var delay = TimeSpan.FromMinutes(2);
                Console.WriteLine($"Secondary rate limit exceeded. Waiting for {delay}...");
                await Task.Delay(delay);
            }
        }
    }

    private enum SearchBy
    {
        Created = 0,
        Updated,
    }

    public sealed record Pull(string Owner, string Repo, int Number, DateTimeOffset Created, string State, bool Merged, string Url)
    {
        public string Key => $"{Owner}/{Repo}#{Number}";

        public string Repository => $"{Owner}/{Repo}";
    }

    public sealed record Repo(string Owner, string Name, string Language, string Url)
    {
        public string Key => $"{Owner}/{Name}";
    }

    private sealed record SyncState(DateTimeOffset? OldestCreated, DateTimeOffset? LastSyncedAt, bool BackfillComplete);

    private sealed class TokenCredentialStore(string accessToken) : ICredentialStore
    {
        private readonly Credentials _credentials = new(accessToken);

        public bool Anonymous { get; set; }

        public Task<Credentials> GetCredentials()
            => Task.FromResult(Anonymous ? Credentials.Anonymous : _credentials);
    }
}
