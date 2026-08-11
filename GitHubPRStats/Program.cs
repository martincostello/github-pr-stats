using System.Text;
using GitHubPRStats;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .Build();

var token = configuration["GITHUB_TOKEN"] ?? throw new InvalidOperationException("No GitHub token specified.");
var index = new Cache(".github-data", token);

bool outputMarkdown = args.Contains("--markdown", StringComparer.OrdinalIgnoreCase);
bool republish = args.Contains("--republish", StringComparer.OrdinalIgnoreCase);
bool publish = republish || args.Contains("--publish", StringComparer.OrdinalIgnoreCase);

var console = AnsiConsole.Console;

using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    if (cancellation.IsCancellationRequested)
    {
        return;
    }

    e.Cancel = true;

    console.MarkupLine("[yellow]Stopping at the next safe point... press Ctrl+C again to quit immediately.[/]");
    cancellation.Cancel();
};

var cancellationToken = cancellation.Token;

if (args.Contains("--index", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        await index.BuildAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Cancelled(console);
    }
}

var markdown = new StringBuilder();

var pulls = await index.GetPullsAsync();

if (pulls.Count is 0)
{
    console.MarkupLine("[yellow]The cache is empty. Run the tool with [bold]--index[/] to hydrate it from GitHub.[/]");
    return 0;
}

var user = await index.CurrentUserAsync();

markdown.AppendLine("# GitHub PR Stats")
        .AppendLine()
        .AppendLine("## Summary")
        .AppendLine()
        .AppendLine($"Found **{pulls.Count}** pull requests for [@{user.Login}]({user.HtmlUrl}).")
        .AppendLine();

console.MarkupLineInterpolated($"Found [green]{pulls.Count}[/] pull requests for [link={user.HtmlUrl}]@{user.Login}[/].");
console.WriteLine();

var first = pulls.MinBy((p) => p.Created)!;

console.MarkupLineInterpolated($"First pull request: [link={first.Url}]{first.Owner}/{first.Repo}#{first.Number}[/]");
console.WriteLine();

markdown.AppendLine($"- First pull request: [{first.Owner}/{first.Repo}#{first.Number}]({first.Url})");

var owners = pulls.GroupBy((p) => p.Owner)
                  .OrderByDescending((p) => p.Count())
                  .ThenBy((p) => p.Key)
                  .ToList();

var mostPopularOwner = owners.MaxBy((p) => p.Count())!;
var mostPopularOwnerCount = mostPopularOwner.Count();
var mostPopularOwnerUrl = $"https://github.com/{mostPopularOwner.Key}";

console.MarkupLineInterpolated($"Most popular organisation/user: [link={mostPopularOwnerUrl}]{mostPopularOwner.Key}[/] ({mostPopularOwnerCount})");
console.WriteLine();

markdown.AppendLine($"- Most popular organisation/user: [{mostPopularOwner.Key}]({mostPopularOwnerUrl}) ({mostPopularOwnerCount})");

var mostPopularRepo = pulls.GroupBy((p) => $"{p.Owner}/{p.Repo}").MaxBy((p) => p.Count())!;
var mostPopularRepoCount = mostPopularRepo.Count();
var mostPopularRepoUrl = $"https://github.com/{mostPopularRepo.Key}";

console.MarkupLineInterpolated($"Most popular repository: [link={mostPopularRepoUrl}]{mostPopularRepo.Key}[/] ({mostPopularRepoCount})");
console.WriteLine();

markdown.AppendLine($"- Most popular repository: [{mostPopularRepo.Key}]({mostPopularRepoUrl}) ({mostPopularRepoCount})");

int chartWidth = 60;

markdown.AppendLine("## By Owner")
        .AppendLine();

var ownersBarChart = new BarChart()
    .Width(chartWidth)
    .Label($"[green bold underline]Pull requests by owner ({pulls.Count})[/]");

var ownersPieChart = new StringBuilder()
    .AppendLine("```mermaid")
    .AppendLine("pie")
    .AppendLine($"title By repository owner ({pulls.Count})");

var ownersTable = new StringBuilder()
    .AppendLine("| **Owner** | **Count** | **Percent** |")
    .AppendLine("| :-------- | --------: | ----------: |");

Color[] colors = [Color.Orange1, Color.Blue, Color.Purple, Color.Green, Color.Red, Color.Yellow];

int others = 0;

foreach ((var i, var owner) in owners.Index())
{
    double count = owner.Count();
    var ownerUrl = $"https://github.com/{owner.Key}";

    ownersBarChart.AddItem(
        $"[link={ownerUrl}]{owner.Key}[/] ({count / pulls.Count:P1})",
        count,
        colors[i % colors.Length]);

    if ((count / pulls.Count) < 0.01)
    {
        others += (int)count;
    }
    else
    {
        ownersPieChart.AppendLine($"    \"{owner.Key}\": {count}");
    }

    ownersTable.AppendLine($"| [{owner.Key}]({ownerUrl}) | {count:N0} | {count / pulls.Count:P1} |");
}

if (others > 0)
{
    ownersPieChart.AppendLine($"    \"Others\": {others}");
}

console.Write(ownersBarChart);
console.WriteLine();

markdown.Append(ownersPieChart)
        .AppendLine("```")
        .AppendLine()
        .AppendDetails("Pull requests by repository owner", (builder) => builder.Append(ownersTable));

var state = pulls.CountBy((p) => p.State).ToDictionary();
var statesChart = new BarChart()
    .Width(chartWidth)
    .Label("[bold underline]State[/]")
    .AddItem($"Closed ({(float)state["closed"] / pulls.Count:P1})", state["closed"], Color.Green)
    .AddItem($"Open ({(float)state["open"] / pulls.Count:P1})", state["open"], Color.Yellow);

console.Write(statesChart);
console.WriteLine();

markdown.AppendLine("## By State")
        .AppendLine()
        .AppendBarChart("By State", Math.Max(state["closed"], state["open"]), ["Closed", "Open"], [state["closed"], state["open"]])
        .AppendDetails(
            "Pull requests by state",
            (builder) => builder.AppendLine("| **State** | **Count**         | **Percent**                               |")
                                .AppendLine("| :-------- | ----------------: | ----------------------------------------: |")
                                .AppendLine($"| Closed   | {state["closed"]} | {(float)state["closed"] / pulls.Count:P1} |")
                                .AppendLine($"| Open     | {state["open"]}   | {(float)state["open"] / pulls.Count:P1}   |"));

var merged = pulls.CountBy((p) => p.Merged)
                  .ToDictionary();

var mergedChart = new BarChart()
    .Width(chartWidth)
    .Label($"[bold underline]Merged?[/]")
    .AddItem($"Yes ({(float)merged[true] / pulls.Count:P1})", merged[true], Color.Purple)
    .AddItem($"No ({(float)merged[false] / pulls.Count:P1})", merged[false], Color.Silver);

console.Write(mergedChart);
console.WriteLine();

markdown.AppendBarChart("Merged?", Math.Max(merged[true], merged[false]), ["Yes", "No"], [merged[true], merged[false]])
        .AppendDetails(
            "Pull requests merged",
            (builder) => builder.AppendLine("| **Merged?** | **Count**       | **Percent**                             |")
                                .AppendLine("| :---------- | --------------: | --------------------------------------: |")
                                .AppendLine($"| Yes        | {merged[true]}  | {(float)merged[true] / pulls.Count:P1}  |")
                                .AppendLine($"| No         | {merged[false]} | {(float)merged[false] / pulls.Count:P1} |"));

var years = pulls.CountBy((p) => p.Created.Year)
                 .OrderBy((p) => p.Key)
                 .ToList();

var yearsBarChart = new BarChart()
    .Width(chartWidth)
    .Label($"[bold underline]By year[/]");

var yearsTable = new StringBuilder()
    .AppendLine("| **Year** | **Count** | **Percent** |")
    .AppendLine("| :------- | --------: | ----------: |");

colors = [Color.Red, Color.Yellow, Color.Green, Color.Blue, Color.Purple];

foreach ((var i, (var year, var count)) in years.Index())
{
    yearsBarChart.AddItem(
        $"{year} ({(float)count / pulls.Count,5:P1})",
        count,
        colors[i % colors.Length]);

    yearsTable.AppendLine($"| {year} | {count:N0} | {(float)count / pulls.Count,5:P1} |");
}

console.Write(yearsBarChart);
console.WriteLine();

markdown.AppendLine("## By Year")
        .AppendLine()
        .AppendBarChart("Pull requests by year", years.MaxBy((p) => p.Value).Value, years.Select((p) => p.Key.ToString()), years.Select((p) => p.Value))
        .AppendDetails("Pull requests by year", (builder) => builder.Append(yearsTable));

var repos = await index.GetReposAsync();

var languages = repos.CountBy((p) => p.Language)
                     .OrderByDescending((p) => p.Value)
                     .ThenBy((p) => p.Key)
                     .ToList();

var languagesBreakdownChart = new BreakdownChart()
    .Width(chartWidth);

var languagesPieChart = new StringBuilder()
    .AppendLine("```mermaid")
    .AppendLine("pie")
    .AppendLine($"title By primary repository language ({languages.Count})");

var languagesTable = new StringBuilder()
    .AppendLine("| **Language** | **Count** | **Percent** |")
    .AppendLine("| :----------- | --------: | ----------: |");

// TODO Get more from https://github.com/github/personal-website/blob/ec99147d789ea3332274857d38aba8c3b5063ae5/_data/colors.json#L155
var languageColors = new Dictionary<string, Color>()
{
    ["C#"] = new(0x17, 0x86, 0x00),
    ["C++"] = new(0xf3, 0x4b, 0x7d),
    ["CSS"] = new(0x56, 0x3d, 0x7c),
    ["Dockerfile"] = new(0x38, 0x4d, 0x54),
    ["F#"] = new(0xb8, 0x45, 0xfc),
    ["Go"] = new(0x00, 0xad, 0xd8),
    ["HTML"] = new(0xe3, 0x4c, 0x26),
    ["Java"] = new(0xb0, 0x72, 0x19),
    ["JavaScript"] = new(0xf1, 0xe0, 0x5a),
    ["Makefile"] = new(0x42, 0x78, 0x19),
    ["Markdown"] = new(0x08, 0x3f, 0xa1),
    ["MDX"] = new(0xfc, 0xb3, 0x2c),
    ["PHP"] = new(0x45, 0xfd, 0x95),
    ["PowerShell"] = new(0x01, 0x24, 0x56),
    ["Python"] = new(0x35, 0x72, 0xa5),
    ["Ruby"] = new(0x70, 0x15,0x16),
    ["Rust"] = new(0xde, 0xa2, 0x54),
    ["Scala"] = new(0xc2, 0x2d,0x40),
    ["SCSS"] = new(0xc6, 0x53, 0x8c),
    ["Shell"] = new(0x89, 0xe0, 0x51),
    ["TypeScript"] = new(0x31, 0x78, 0xc6),
};

others = 0;

foreach ((var i, (var language, var count)) in languages.Index())
{
    float countF = count;

    languagesBreakdownChart.AddItem(
        $"{language} ({countF / repos.Count:P1})",
        count,
        languageColors.TryGetValue(language, out var color) ? color : i % 2 is 0 ? Color.Grey : Color.Silver);

    if ((countF / repos.Count) < 0.01)
    {
        others += count;
    }
    else
    {
        languagesPieChart.AppendLine($"    \"{language}\": {count}");
    }

    languagesTable.AppendLine($"| {language} | {count:N0} | {countF / repos.Count:P1} |");
}

console.MarkupLineInterpolated($"            [bold underline]Repositories' primary language({repos.Count})[/]");
console.Write(languagesBreakdownChart);
console.WriteLine();

if (others > 0)
{
    languagesPieChart.AppendLine($"    \"Others\": {others}");
}

markdown.AppendLine("## By Language")
        .AppendLine()
        .Append(languagesPieChart)
        .AppendLine("```")
        .AppendDetails("Pull requests by language", (builder) => builder.Append(languagesTable));

if (outputMarkdown)
{
    await File.WriteAllTextAsync("summary.md", markdown.ToString());
}

if (publish)
{
    var lokiUrl = new Uri(configuration["LOKI_URL"] ?? "http://localhost:3100", UriKind.Absolute);
    var otlpUrl = new Uri(configuration["OTLP_ENDPOINT"] ?? "http://localhost:4318", UriKind.Absolute);

    // The pull requests already in Loki are tracked locally because Loki is append-only,
    // so re-publishing them would double count them. Use --republish after recreating the
    // Docker volume to hydrate a brand new Grafana instance from the cache.
    var published = republish ? new HashSet<string>(StringComparer.Ordinal) : await index.GetPublishedAsync();
    var repoLanguages = repos.ToDictionary((p) => p.Key, (p) => p.Language, StringComparer.OrdinalIgnoreCase);

    try
    {
        using (var loki = new LokiPublisher(lokiUrl))
        {
            try
            {
                await loki.PublishAsync(pulls, repoLanguages, published, cancellationToken);
            }
            finally
            {
                await index.SavePublishedAsync(published);
            }
        }

        MetricsPublisher.Publish(otlpUrl, pulls, repos, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Cancelled(console);
    }
}

return 0;

static int Cancelled(IAnsiConsole console)
{
    console.MarkupLine("[yellow]Stopped before finishing. Re-run the tool to resume data collection.[/]");
    return 1;
}

static class StringBuilderExtensions
{
    public static StringBuilder AppendBarChart<T>(
        this StringBuilder builder,
        string title,
        int maximumValue,
        IEnumerable<string> xaxis,
        IEnumerable<T> bars)
    {
        return builder.AppendLine("```mermaid")
                      .AppendLine("xychart-beta")
                      .AppendLine($"    title \"{title}\"")
                      .AppendLine($"    x-axis [{string.Join(", ", xaxis.Select((p) => $"\"{p}\""))}]")
                      .AppendLine($"    y-axis \"Count\" 0 --> {maximumValue}")
                      .AppendLine($"    bar [{string.Join(", ", bars)}]")
                      .AppendLine("```")
                      .AppendLine();
    }

    public static StringBuilder AppendDetails(this StringBuilder builder, string title, Action<StringBuilder> content)
    {
        builder.AppendLine()
               .AppendLine("<details>")
               .AppendLine()
               .AppendLine($"<summary>{title}</summary>")
               .AppendLine();

        content(builder);

        return builder.AppendLine()
                      .AppendLine("</details>")
                      .AppendLine()
                      .AppendLine();
    }

    public static StringBuilder AppendPieChart(this StringBuilder builder, string value)
    {
        builder.AppendLine(value);
        return builder;
    }
}
