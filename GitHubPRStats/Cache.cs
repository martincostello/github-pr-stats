using System.Globalization;
using System.Text.Json.Nodes;
using Octokit;
using FileMode = System.IO.FileMode;

namespace GitHubPRStats;

internal sealed class Cache
{
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

    private const string CursorFileName = "cursor";
    private const string IndexFileName = "pulls-index.json";
    private const string ReposFileName = "repos.json";

    public async Task BuildAsync()
    {
        if (!Directory.Exists(_path))
        {
            Directory.CreateDirectory(_path);
        }

        var repos = await BuildPullsAsync();

        await BuildReposAsync(repos);
    }

    public async Task<User> CurrentUserAsync()
        => _user ??= await _github.User.Current();

    public async Task<IReadOnlyList<Pull>> GetPullsAsync()
    {
        var lines = await File.ReadAllLinesAsync(Path.Join(_path, IndexFileName));

        var result = new List<Pull>(lines.Length);

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            var json = JsonNode.Parse(line)!;

            result.Add(new(
                json["owner"]!.GetValue<string>(),
                json["repo"]!.GetValue<string>(),
                json["number"]!.GetValue<int>(),
                json["created"]!.GetValue<DateTimeOffset>(),
                json["state"]!.GetValue<string>(),
                json["merged"]!.GetValue<bool>(),
                json["url"]!.GetValue<string>()));
        }

        return result;
    }

    public async Task<IReadOnlyList<Repo>> GetReposAsync()
    {
        var lines = await File.ReadAllLinesAsync(Path.Join(_path, ReposFileName));

        var result = new List<Repo>(lines.Length);

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            var json = JsonNode.Parse(line)!;

            result.Add(new(
                json["owner"]!.GetValue<string>(),
                json["name"]!.GetValue<string>(),
                json["language"]?.GetValue<string>() ?? "Unknown",
                json["url"]!.GetValue<string>()));
        }

        return result;
    }

    private async Task BuildReposAsync(HashSet<(string Owner, string Repo)> repos)
    {
        using var file = File.Open(Path.Join(_path, ReposFileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file);

        Console.WriteLine($"Caching {repos.Count} repositories...");

        foreach (var (owner, name) in repos)
        {
            Repository? repo = null;

            while (repo is null)
            {
                try
                {
                    try
                    {
                        repo = await _github.Repository.Get(owner, name);
                    }
                    catch (ForbiddenException)
                    {
                        // Repo only allows fine-grained access tokens
                        _credentials.Anonymous = true;
                        repo = await _github.Repository.Get(owner, name);
                    }
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
                finally
                {
                    _credentials.Anonymous = false;
                }
            }

            var json = new JsonObject()
            {
                ["owner"] = repo.Owner.Login,
                ["name"] = repo.Name,
                ["language"] = repo.Language,
                ["url"] = repo.HtmlUrl,
            };

            await writer.WriteLineAsync(json.ToJsonString());
            await writer.FlushAsync();
        }
    }

    private async Task<HashSet<(string Owner, string Repo)>> BuildPullsAsync()
    {
        using var file = File.Open(Path.Join(_path, IndexFileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file);

        var me = await CurrentUserAsync();

        var created = DateTimeOffset.UtcNow;

        var cursor = Path.Combine(_path, CursorFileName);
        if (File.Exists(cursor))
        {
            created = DateTimeOffset.Parse(await File.ReadAllTextAsync(cursor), CultureInfo.InvariantCulture);
        }

        var repos = new HashSet<(string Owner, string Repo)>();

        do
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
                Created = new DateRange(created.AddMonths(-1), created),
            };

            Console.WriteLine($"Searching for pull requests created {created}...");

            int count = 0;

            while (true)
            {
                SearchIssuesResult pulls;

                try
                {
                    pulls = await _github.Search.SearchIssues(query);
                }
                catch (RateLimitExceededException ex)
                {
                    var delay = ex.GetRetryAfterTimeSpan().Add(TimeSpan.FromSeconds(2));
                    Console.WriteLine($"Rate limit exceeded. Waiting for {delay}...");
                    await Task.Delay(delay);
                    continue;
                }
                catch (SecondaryRateLimitExceededException)
                {
                    var delay = TimeSpan.FromMinutes(2);
                    Console.WriteLine($"Secondary rate limit exceeded. Waiting for {delay}...");
                    await Task.Delay(delay);
                    continue;
                }

                if (pulls.Items.Count == 0)
                {
                    break;
                }

                foreach (var pull in pulls.Items)
                {
                    // E.g. https://github.com/dotnet/aspnetcore/pull/56395
                    var segments = pull.HtmlUrl.Split('/');
                    var owner = segments[3];
                    var repo = segments[4];
                    var number = segments[6];

                    if (owner != me.Login)
                    {
                        var json = new JsonObject()
                        {
                            ["owner"] = owner,
                            ["repo"] = repo,
                            ["number"] = int.Parse(number, CultureInfo.InvariantCulture),
                            ["created"] = pull.CreatedAt,
                            ["state"] = pull.State.ToString(),
                            ["merged"] = pull.PullRequest.Merged,
                            ["url"] = pull.PullRequest.HtmlUrl,
                        };

                        await writer.WriteLineAsync(json.ToJsonString());

                        repos.Add((owner, repo));
                    }
                }

                await writer.FlushAsync();

                count += pulls.Items.Count;

                if (pulls.Items.Count < query.PerPage)
                {
                    break;
                }

                query.Page++;
            }

            Console.WriteLine($"Found {count} pull requests.");

            await File.WriteAllTextAsync(cursor, created.ToString("u", CultureInfo.InvariantCulture));
            created = created.AddMonths(-1);
        }
        while (created > me.CreatedAt);

        await writer.FlushAsync();

        return repos;
    }

    public sealed record Pull(string Owner, string Repo, int Number, DateTimeOffset Created, string State, bool Merged, string Url)
        : PullIndex(Owner, Repo, Number, Created, State, Merged, Url);

    public record PullIndex(
        string Owner,
        string Repo,
        int Number,
        DateTimeOffset Created,
        string State,
        bool Merged,
        string Url);

    public sealed record Repo(string Owner, string Name, string Language, string Url);

    private sealed class TokenCredentialStore(string accessToken) : ICredentialStore
    {
        private readonly Credentials _credentials = new(accessToken);

        public bool Anonymous { get; set; }

        public Task<Credentials> GetCredentials()
            => Task.FromResult(Anonymous ? Credentials.Anonymous : _credentials);
    }
}
