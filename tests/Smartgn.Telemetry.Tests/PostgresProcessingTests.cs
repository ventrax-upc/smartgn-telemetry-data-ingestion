using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Smartgn.Telemetry.Application;
using Smartgn.Telemetry.Domain;
using Smartgn.Telemetry.Infrastructure;

namespace Smartgn.Telemetry.Tests;

public class PostgresProcessingTests
{
    [PostgresFact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentReplayAndStageRecoveryPreservePendingReadings()
    {
        var connectionString = Environment.GetEnvironmentVariable("TELEMETRY_TEST_DB")!;

        // A separate schema lets the fixture run repeatedly without touching other tables.
        await using var server = NpgsqlDataSource.Create(connectionString);
        var schema = $"telemetry_test_{Guid.NewGuid():N}";
        await ExecuteAsync(server, $"CREATE SCHEMA {schema}");
        try
        {
            var options = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ct = timeout.Token;
            PostgresTelemetryInbox Inbox(NpgsqlDataSource database) =>
                new(database, new(2, 1, 1, 1),
                    services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
                    NullLogger<PostgresTelemetryInbox>.Instance);

            var now = DateTimeOffset.UtcNow;
            var first = new TelemetryReading("meter", "first", 1, now, now,
                new Dictionary<string, double> { ["pressureKpa"] = 3 }, TelemetryFreshness.Current, 1);
            var second = first with { EventId = "second", SequenceNumber = 2 };
            await using (var database = NpgsqlDataSource.Create(options.ConnectionString))
            {
                var inbox = Inbox(database);
                await inbox.InitializeAsync(ct);
                var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => inbox.EnqueueAsync(first, ct)));
                Assert.Single(outcomes, value => value == IngestionOutcome.Accepted);
                Assert.Equal(7, outcomes.Count(value => value == IngestionOutcome.Duplicate));
                Assert.Equal(IngestionOutcome.Conflict,
                    await inbox.EnqueueAsync(first with { SequenceNumber = 99 }, ct));
                Assert.Equal(IngestionOutcome.Accepted, await inbox.EnqueueAsync(second, ct));
                Assert.Equal(IngestionOutcome.StoreUnavailable,
                    await inbox.EnqueueAsync(first with { EventId = "third" }, ct));

                Assert.True(await inbox.ProcessNextAsync("influx",
                    (_, _) => throw new IOException("Simulated unavailable store"), ct));
                Assert.Equal(2, await inbox.CountPendingAsync("meter", 1, ct));
            }

            await using (var database = NpgsqlDataSource.Create(options.ConnectionString))
            {
                var inbox = Inbox(database);
                await inbox.InitializeAsync(ct);
                await ExecuteAsync(database, "SELECT telemetry_requeue('meter', 'first')");
                var slots = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
                Task Write(TelemetryReading reading, CancellationToken _)
                {
                    Assert.Equal(now, reading.MeasuredAt);
                    Assert.True(slots.TryAdd(reading.EventId, reading.PointSlot));
                    return Task.CompletedTask;
                }

                await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => inbox.ProcessNextAsync("influx", Write, ct)));
                Assert.Equal(0, slots["first"]);
                Assert.Equal(1, slots["second"]);
                var receipts = new HashSet<string>();
                Assert.True(await inbox.ProcessNextAsync("alerting", (reading, _) =>
                {
                    receipts.Add(reading.EventId);
                    throw new IOException("Simulated lost HTTP response after the receiver committed");
                }, ct));
                foreach (var eventId in receipts)
                {
                    await ExecuteAsync(database, $"SELECT telemetry_requeue('meter', '{eventId}')");
                }

                async Task Receive(TelemetryReading reading, CancellationToken _)
                {
                    receipts.Add(reading.EventId);
                    await Task.CompletedTask;
                }

                while (await inbox.ProcessNextAsync("alerting", Receive, ct))
                {
                }

                Assert.Equal(2, receipts.Count);
                Assert.Equal(0, await inbox.CountPendingAsync("meter", 1, ct));
                Assert.Equal(IngestionOutcome.Duplicate, await inbox.EnqueueAsync(first, ct));
                Assert.Equal(IngestionOutcome.Accepted,
                    await inbox.EnqueueAsync(first with { EventId = "third" }, ct));
            }
        }
        finally
        {
            await ExecuteAsync(server, $"DROP SCHEMA {schema} CASCADE");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource database, string sql)
    {
        await using var command = database.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEMETRY_TEST_DB")))
        {
            Skip = "Set TELEMETRY_TEST_DB to a disposable PostgreSQL database.";
        }
    }
}