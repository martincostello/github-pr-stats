using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace GitHubPRStats;

/// <summary>
/// Publishes pull requests to Loki as log entries timestamped with the date each pull request
/// was created, so the entire history is stored in Grafana and can be queried with LogQL.
/// </summary>
/// <remarks>
/// Only immutable facts about a pull request are stored here; anything that can change over its
/// lifetime (such as whether it is open or merged) is published as a metric by
/// <see cref="MetricsPublisher"/> instead, because Loki entries cannot be updated once written.
/// </remarks>
internal sealed class LokiPublisher(Uri endpoint) : IDisposable
{
    /// <summary>
    /// The value of the <c>service_name</c> label applied to every entry.
    /// </summary>
    public const string ServiceName = "github-pr-stats";

    private const int BatchSize = 500;

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>
    /// Publishes any pull requests that are not already in <paramref name="published"/>, adding
    /// the key of each successfully published pull request to that set as it goes.
    /// </summary>
    public async Task<int> PublishAsync(
        IReadOnlyList<Cache.Pull> pulls,
        IReadOnlyDictionary<string, string> languages,
        HashSet<string> published,
        CancellationToken cancellationToken)
    {
        var pending = pulls.Where((p) => !published.Contains(p.Key))
                           .OrderBy((p) => p.Created)
                           .ToList();

        if (pending.Count is 0)
        {
            Console.WriteLine($"Loki at {endpoint} is already up-to-date.");
            return 0;
        }

        var url = new Uri(endpoint, "/loki/api/v1/push");

        Console.WriteLine($"Publishing {pending.Count:N0} pull requests to Loki at {endpoint}...");

        int count = 0;

        foreach (var batch in pending.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var streams = new JsonArray();

            // Keep the number of streams low by only using labels of a low cardinality;
            // everything else is part of the log line and extracted at query time.
            foreach (var group in batch.GroupBy((p) => (p.Owner, Language: languages.GetValueOrDefault(p.Repository, "Unknown"))))
            {
                var values = new JsonArray();

                foreach (var pull in group.OrderBy((p) => p.Created))
                {
                    var line = new JsonObject()
                    {
                        ["owner"] = pull.Owner,
                        ["repo"] = pull.Repo,
                        ["repository"] = pull.Repository,
                        ["number"] = pull.Number,
                        ["url"] = pull.Url,
                    };

                    values.Add(new JsonArray(
                        pull.Created.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "000000",
                        line.ToJsonString()));
                }

                streams.Add(new JsonObject()
                {
                    ["stream"] = new JsonObject()
                    {
                        ["service_name"] = ServiceName,
                        ["owner"] = group.Key.Owner,
                        ["language"] = group.Key.Language,
                    },
                    ["values"] = values,
                });
            }

            var payload = new JsonObject() { ["streams"] = streams };

            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync(url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(CancellationToken.None);
                throw new InvalidOperationException(
                    $"Failed to publish {batch.Length} pull requests to Loki. {(int)response.StatusCode} {response.ReasonPhrase}: {error}");
            }

            foreach (var pull in batch)
            {
                published.Add(pull.Key);
            }

            count += batch.Length;
        }

        Console.WriteLine($"Published {count:N0} pull requests to Loki.");

        return count;
    }

    public void Dispose() => _client.Dispose();
}
