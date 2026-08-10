# GitHub PR Stats

A tool that works out statistics about the pull requests you have opened in other people's
GitHub repositories, renders them to the console, and stores them in a local Grafana instance.

## Usage

Set a GitHub personal access token, either as the `GITHUB_TOKEN` environment variable or as a
user secret:

```console
dotnet user-secrets set GITHUB_TOKEN "<your-token>" --project GitHubPRStats
```

Then hydrate the local cache from GitHub and publish it to Grafana:

```console
docker compose up -d
dotnet run --project GitHubPRStats -- --index --publish
```

Open <http://localhost:3000> and the dashboard is the Grafana home page.

| Option | Description |
| :----- | :---------- |
| `--index` | Fetch pull requests from GitHub into the local cache in `.github`. |
| `--publish` | Publish the cache to Grafana. |
| `--republish` | Publish _everything_ in the cache to Grafana, ignoring what has been published before. |
| `--markdown` | Write a `summary.md` file as well as printing to the console. |

With no options the tool just prints the statistics already in the local cache.

## Re-running it later

Running `--index --publish` again is cheap and incremental, so it is fine to re-run it whenever
you want to bring the data up-to-date:

- The **first** run walks backwards from today to the date your account was created, a month at
  a time, because the GitHub search API only ever returns the first 1,000 matches per query. It
  checkpoints as it goes, so if it is interrupted the next run picks up where it left off.
- **Subsequent** runs only search for pull requests that have been created or updated since the
  previous run, which is usually a single API call. Repositories are only looked up the first
  time a pull request is seen for them.
- Only pull requests that have not already been sent to Grafana are published.

## How the data is stored

The `docker-compose.yml` runs [`grafana/otel-lgtm`][docker-otel-lgtm], which bundles Grafana,
Loki, Prometheus, Tempo and an OpenTelemetry collector into a single container. Everything it
persists is written to `./data/lgtm`, which is mapped into the container as `/data`, so the data
survives the container being recreated.

The pull requests are split across two of those backends, based on what can change over time:

- **Loki** stores one entry per pull request, timestamped with the date the pull request was
  opened. This is the history, and it only contains facts that never change - which repository
  the pull request was opened against, its number and its URL. Loki is append-only, so the tool
  keeps track of what it has already published in `.github/published.json` and never sends the
  same pull request twice.
- **Prometheus** stores the aggregated counts - by state, by whether it was merged, by owner, by
  repository, by language and by year - written over OTLP each time the tool runs. These are
  recomputed from the whole cache every time, so anything that has changed since a pull request
  was first seen (such as it being merged) is always up-to-date. As a side effect, running the
  tool periodically also builds up a history of how the totals have grown over time.

`grafana/loki-config.yaml` raises a few Loki limits that get in the way of storing a history
this old, most importantly turning off the rejection of entries older than a week, and starting
the schema in 2007 so entries predating Loki's default schema date can be written at all.

To start from scratch, delete the volume and re-publish the cache:

```console
docker compose down
rm -rf ./data
docker compose up -d
dotnet run --project GitHubPRStats -- --republish
```

## The dashboard

`grafana/dashboards/github-pr-stats.json` is provisioned into Grafana and set as the home
dashboard, so it loads as soon as you open <http://localhost:3000>. It has a panel for each of
the summaries the tool prints to the console.

Anonymous access is enabled with the `Viewer` role so the dashboard is there without signing in.
Sign in as `admin`/`admin` to edit anything. This stack is intended to be run locally only.

[docker-otel-lgtm]: https://github.com/grafana/docker-otel-lgtm
