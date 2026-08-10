using System.Diagnostics.Metrics;
using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace GitHubPRStats;

/// <summary>
/// Publishes a snapshot of the aggregated pull request statistics to Prometheus using OTLP.
/// </summary>
/// <remarks>
/// Unlike the log entries written by <see cref="LokiPublisher"/>, these values are recomputed
/// from the whole cache and stamped with the time of the run, so they are always up-to-date for
/// pull requests whose state has changed since they were first seen. Running the tool
/// periodically also builds up a history of how the totals have grown over time.
/// </remarks>
internal static class MetricsPublisher
{
    private const string MeterName = "GitHubPRStats";

    public static void Publish(Uri endpoint, IReadOnlyList<Cache.Pull> pulls, IReadOnlyList<Cache.Repo> repos)
    {
        var languages = repos.ToDictionary((p) => p.Key, (p) => p.Language, StringComparer.OrdinalIgnoreCase);

        using var provider = Sdk
            .CreateMeterProviderBuilder()
            .ConfigureResource((p) => p.AddService(LokiPublisher.ServiceName, autoGenerateServiceInstanceId: false))
            .AddMeter(MeterName)
            .AddOtlpExporter((exporter, reader) =>
            {
                exporter.Endpoint = new Uri(endpoint, "/v1/metrics");
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;

                // Only export when the metrics are explicitly flushed below.
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = Timeout.Infinite;
            })
            .Build();

        using var meter = new Meter(MeterName);

        meter.CreateObservableGauge(
            "github.pull_requests",
            () => pulls.Count,
            description: "The total number of pull requests opened.");

        meter.CreateObservableGauge(
            "github.pull_requests.by_state",
            () => Measure(pulls.CountBy((p) => p.State is "open" ? "Open" : "Closed"), "state"),
            description: "The number of pull requests by their current state.");

        meter.CreateObservableGauge(
            "github.pull_requests.by_merged",
            () => Measure(pulls.CountBy((p) => p.Merged ? "Yes" : "No"), "merged"),
            description: "The number of pull requests by whether they were merged.");

        meter.CreateObservableGauge(
            "github.pull_requests.by_owner",
            () => Measure(pulls.CountBy((p) => p.Owner), "owner"),
            description: "The number of pull requests by repository owner.");

        meter.CreateObservableGauge(
            "github.pull_requests.by_repository",
            () => Measure(pulls.CountBy((p) => p.Repository), "repository"),
            description: "The number of pull requests by repository.");

        meter.CreateObservableGauge(
            "github.pull_requests.by_language",
            () => Measure(pulls.CountBy((p) => languages.GetValueOrDefault(p.Repository, "Unknown")), "language"),
            description: "The number of pull requests by the repository's primary language.");

        meter.CreateObservableGauge(
            "github.pull_requests.by_year",
            () => Measure(pulls.CountBy((p) => p.Created.Year.ToString(CultureInfo.InvariantCulture)), "year"),
            description: "The number of pull requests by the year they were created.");

        meter.CreateObservableGauge(
            "github.repositories",
            () => repos.Count,
            description: "The total number of repositories contributed to.");

        meter.CreateObservableGauge(
            "github.owners",
            () => pulls.DistinctBy((p) => p.Owner).Count(),
            description: "The total number of organisations and users contributed to.");

        var first = pulls.MinBy((p) => p.Created)!;

        meter.CreateObservableGauge(
            "github.pull_requests.first_created",
            () => new Measurement<double>(
                first.Created.ToUnixTimeMilliseconds(),
                new KeyValuePair<string, object?>("repository", first.Repository),
                new KeyValuePair<string, object?>("number", first.Number.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, object?>("url", first.Url)),
            description: "When the very first pull request was opened, as Unix time in milliseconds.");

        Console.WriteLine($"Publishing metrics to {endpoint} using OTLP...");

        if (!provider.ForceFlush(30_000))
        {
            throw new InvalidOperationException($"Failed to publish metrics to {endpoint} before the timeout elapsed.");
        }

        Console.WriteLine("Published metrics.");
    }

    private static IEnumerable<Measurement<int>> Measure(IEnumerable<KeyValuePair<string, int>> counts, string name)
        => counts.Select((p) => new Measurement<int>(p.Value, new KeyValuePair<string, object?>(name, p.Key)));
}
