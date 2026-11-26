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
}
