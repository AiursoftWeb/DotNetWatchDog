using System.Globalization;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DotNetWatchDog.Tests;

[TestClass]
public class IncidentMitigationTests
{
    [TestMethod]
    public async Task ZeroErrorsMitigatesActiveIncidentInsteadOfResolving()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var incidentId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var version = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 26, 19, 0, 0, TimeSpan.Zero);
        var metadata = new JObject
        {
            ["source"] = "fixture-source",
            ["node"] = "fixture",
            ["database"] = "AspNetCoreLogs",
            ["table"] = "TracerLogs"
        };
        var context = new JObject
        {
            ["WindowEndUtc"] = now.AddMinutes(-15).ToString("O")
        };

        MitigateIncidentRequest? receivedMitigate = null;
        var resolveCalled = false;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/api/v1/incidents/{id:guid}", () =>
            Results.Text(JsonConvert.SerializeObject(new
            {
                Code = 2,
                Message = "Found",
                Incident = new
                {
                    IncidentId = incidentId,
                    ReportId = reportId,
                    Title = "[fixture] TracerLogs: 5 errors in 15 minutes",
                    Content = "Initial alert",
                    Level = "sev3",
                    Context = context,
                    Metadata = metadata,
                    OccurredAtUtc = now.AddMinutes(-15),
                    ReceivedAtUtc = now.AddMinutes(-15),
                    Status = "new",
                    Version = version
                }
            }), "application/json"));
        app.MapPost("/api/v1/incidents/{id:guid}/mitigate", async (HttpRequest httpRequest) =>
        {
            using var reader = new StreamReader(httpRequest.Body);
            receivedMitigate = JsonConvert.DeserializeObject<MitigateIncidentRequest>(await reader.ReadToEndAsync());
            return Results.Text(JsonConvert.SerializeObject(new
            {
                Code = 2,
                Message = "Mitigated",
                IncidentId = incidentId,
                Status = "mitigated",
                Version = Guid.NewGuid()
            }), "application/json");
        });
        app.MapPost("/api/v1/incidents/{id:guid}/resolve", () =>
        {
            resolveCalled = true;
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        });

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var settings = new Settings("fixture", "AspNetCoreLogs", "Host=unused",
                address, "fixture-key", directory, null, "fixture-source");
            using var store = new StateStore(settings);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAlertCenterSdk(address, "fixture-key");
            using var provider = services.BuildServiceProvider();
            var reporter = new IncidentReporter(
                provider.GetRequiredService<AlertCenterApiClient>(),
                settings, store, new ClickHouseLogReader(settings),
                NullLogger<IncidentReporter>.Instance);

            var active = new IncidentDetails
            {
                IncidentId = incidentId,
                ReportId = reportId,
                Title = "[fixture] TracerLogs: 5 errors in 15 minutes",
                Content = "Initial alert",
                Level = "sev3",
                Context = context,
                Metadata = metadata,
                OccurredAtUtc = now.AddMinutes(-15),
                ReceivedAtUtc = now.AddMinutes(-15),
                Status = "new",
                Version = version
            };

            await reporter.ProcessTableAsync("TracerLogs", new ErrorCounts(0, 0, 0), [active], now, CancellationToken.None);

            Assert.IsFalse(resolveCalled, "DotNetWatchDog should mitigate, not resolve, on recovery.");
            Assert.IsNotNull(receivedMitigate);
            Assert.AreEqual(version, receivedMitigate.Version);
            Assert.IsTrue(receivedMitigate.Reason.Contains("Recovery confirmed."));
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetActiveIncidentsIgnoresMitigatedAndResolvedAndStopsAfterSeenMatchesTotal()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var mitigatedId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var secondPageActiveId = Guid.NewGuid();
        var resolvedId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 28, 6, 30, 0, TimeSpan.Zero);
        var metadata = new JObject
        {
            ["source"] = "clickhouse-app-log-error-monitor",
            ["node"] = "moog",
            ["database"] = "AspNetCoreLogs",
            ["table"] = "RmsLogs"
        };

        var requestedPages = new List<int>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/api/v1/incidents", (HttpRequest httpRequest) =>
        {
            var page = int.Parse(httpRequest.Query["page"].ToString(), CultureInfo.InvariantCulture);
            requestedPages.Add(page);

            object[] items = page == 1
                ?
                [
                    new
                    {
                        IncidentId = mitigatedId,
                        ReportId = Guid.NewGuid(),
                        Title = "[moog] RmsLogs: old mitigated",
                        Level = "sev4",
                        Metadata = metadata,
                        OccurredAtUtc = now.AddHours(-2),
                        ReceivedAtUtc = now.AddHours(-2),
                        Status = "mitigated",
                        Version = Guid.NewGuid()
                    },
                    new
                    {
                        IncidentId = activeId,
                        ReportId = Guid.NewGuid(),
                        Title = "[moog] RmsLogs: active",
                        Level = "sev4",
                        Metadata = metadata,
                        OccurredAtUtc = now.AddMinutes(-10),
                        ReceivedAtUtc = now.AddMinutes(-10),
                        Status = "new",
                        Version = Guid.NewGuid()
                    },
                    new
                    {
                        IncidentId = resolvedId,
                        ReportId = Guid.NewGuid(),
                        Title = "[moog] RmsLogs: resolved",
                        Level = "sev4",
                        Metadata = metadata,
                        OccurredAtUtc = now.AddHours(-5),
                        ReceivedAtUtc = now.AddHours(-5),
                        Status = "resolved",
                        Version = Guid.NewGuid()
                    }
                ]
                :
                [
                    new
                    {
                        IncidentId = secondPageActiveId,
                        ReportId = Guid.NewGuid(),
                        Title = "[moog] OtherLogs: active on page 2",
                        Level = "sev3",
                        Metadata = new JObject
                        {
                            ["source"] = "clickhouse-app-log-error-monitor",
                            ["node"] = "moog",
                            ["database"] = "AspNetCoreLogs",
                            ["table"] = "OtherLogs"
                        },
                        OccurredAtUtc = now.AddMinutes(-5),
                        ReceivedAtUtc = now.AddMinutes(-5),
                        Status = "new",
                        Version = Guid.NewGuid()
                    }
                ];

            return Results.Text(JsonConvert.SerializeObject(new
            {
                Code = 2,
                Message = "Incidents retrieved.",
                Page = page,
                PageSize = 100,
                TotalCount = 4,
                Items = items
            }), "application/json");
        });

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var settings = new Settings("moog", "AspNetCoreLogs", "Host=unused",
                address, "fixture-key", directory, null, "clickhouse-app-log-error-monitor");
            using var store = new StateStore(settings);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAlertCenterSdk(address, "fixture-key");
            using var provider = services.BuildServiceProvider();
            var reporter = new IncidentReporter(
                provider.GetRequiredService<AlertCenterApiClient>(),
                settings, store, new ClickHouseLogReader(settings),
                NullLogger<IncidentReporter>.Instance);

            var active = await reporter.GetActiveIncidentsAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 1, 2 }, requestedPages);
            Assert.AreEqual(2, active.Count);
            Assert.IsTrue(active.Any(x => x.IncidentId == activeId));
            Assert.IsTrue(active.Any(x => x.IncidentId == secondPageActiveId));
            Assert.IsFalse(active.Any(x => x.IncidentId == mitigatedId));
            Assert.IsFalse(active.Any(x => x.IncidentId == resolvedId));
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProcessTableIgnoresMitigatedIncidentAlongsideOpenIncidentForSameTable()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var mitigatedId = Guid.NewGuid();
        var openId = Guid.NewGuid();
        var openReportId = Guid.NewGuid();
        var openVersion = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 28, 6, 30, 0, TimeSpan.Zero);
        var metadata = new JObject
        {
            ["source"] = "clickhouse-app-log-error-monitor",
            ["node"] = "moog",
            ["database"] = "AspNetCoreLogs",
            ["table"] = "RmsLogs"
        };

        Guid? mitigatedTarget = null;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/api/v1/incidents/{id:guid}", (Guid id) =>
            Results.Text(JsonConvert.SerializeObject(new
            {
                Code = 2,
                Message = "Found",
                Incident = new
                {
                    IncidentId = id,
                    ReportId = openReportId,
                    Title = "[moog] RmsLogs: 5 errors in 15 minutes",
                    Content = "Open alert",
                    Level = "sev3",
                    Context = new JObject { ["WindowEndUtc"] = now.AddMinutes(-15).ToString("O") },
                    Metadata = metadata,
                    OccurredAtUtc = now.AddMinutes(-15),
                    ReceivedAtUtc = now.AddMinutes(-15),
                    Status = "new",
                    Version = openVersion
                }
            }), "application/json"));
        app.MapPost("/api/v1/incidents/{id:guid}/mitigate", (Guid id) =>
        {
            mitigatedTarget = id;
            return Results.Text(JsonConvert.SerializeObject(new
            {
                Code = 2,
                Message = "Mitigated",
                IncidentId = id,
                Status = "mitigated",
                Version = Guid.NewGuid()
            }), "application/json");
        });

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var settings = new Settings("moog", "AspNetCoreLogs", "Host=unused",
                address, "fixture-key", directory, null, "clickhouse-app-log-error-monitor");
            using var store = new StateStore(settings);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAlertCenterSdk(address, "fixture-key");
            using var provider = services.BuildServiceProvider();
            var reporter = new IncidentReporter(
                provider.GetRequiredService<AlertCenterApiClient>(),
                settings, store, new ClickHouseLogReader(settings),
                NullLogger<IncidentReporter>.Instance);

            var incidents = new[]
            {
                new IncidentDetails
                {
                    IncidentId = mitigatedId,
                    ReportId = Guid.NewGuid(),
                    Title = "[moog] RmsLogs: earlier mitigated alert",
                    Level = "sev4",
                    Metadata = metadata,
                    OccurredAtUtc = now.AddHours(-3),
                    ReceivedAtUtc = now.AddHours(-3),
                    Status = "mitigated",
                    Version = Guid.NewGuid()
                },
                new IncidentDetails
                {
                    IncidentId = openId,
                    ReportId = openReportId,
                    Title = "[moog] RmsLogs: 5 errors in 15 minutes",
                    Level = "sev3",
                    Metadata = metadata,
                    OccurredAtUtc = now.AddMinutes(-15),
                    ReceivedAtUtc = now.AddMinutes(-15),
                    Status = "new",
                    Version = openVersion
                }
            };

            await reporter.ProcessTableAsync("RmsLogs", new ErrorCounts(0, 0, 0), incidents, now, CancellationToken.None);

            Assert.AreEqual(openId, mitigatedTarget);
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
