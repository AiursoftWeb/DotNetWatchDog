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
public class PendingIncidentRetryTests
{
    [TestMethod]
    public async Task RestartRetriesPendingIncidentWithOriginalIdAndStructuredEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var request = new CreateIncidentRequest
        {
            ReportId = Guid.NewGuid(),
            Title = "Fixture error",
            Content = "Saved before restart",
            Level = "sev4",
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Context = new JObject
            {
                ["Errors"] = 3,
                ["Samples"] = new JArray(new JObject { ["message"] = "fixture" })
            },
            Metadata = new JObject
            {
                ["node"] = "fixture",
                ["database"] = "AspNetCoreLogs",
                ["table"] = "TracerLogs"
            }
        };
        var incidentId = Guid.NewGuid();
        CreateIncidentRequest? received = null;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost("/api/v1/incidents", async (HttpRequest httpRequest) =>
        {
            using var reader = new StreamReader(httpRequest.Body);
            received = JsonConvert.DeserializeObject<CreateIncidentRequest>(await reader.ReadToEndAsync());
            return Results.Json(new
            {
                Code = 2, Message = "Received",
                IncidentId = incidentId, request.ReportId,
                ReceivedAtUtc = DateTimeOffset.UtcNow, Duplicate = false
            });
        });
        app.MapGet("/api/v1/incidents/{id:guid}", () =>
            Results.Text(JsonConvert.SerializeObject(new
            {
                Code = 2, Message = "Found",
                Incident = new
                {
                    IncidentId = incidentId, request.ReportId,
                    request.Title, request.Content, request.Level,
                    request.Context, request.Metadata, request.OccurredAtUtc,
                    ReceivedAtUtc = DateTimeOffset.UtcNow, Status = "new", Version = Guid.NewGuid()
                }
            }), "application/json"));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var settings = new Settings("fixture", "AspNetCoreLogs", "Host=unused",
                address, "fixture-key", directory, null, "fixture-source");
            using (var firstProcess = new StateStore(settings))
            {
                firstProcess.Save("TracerLogs", JsonConvert.SerializeObject(request));
            }

            using var restartedProcess = new StateStore(settings);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAlertCenterSdk(address, "fixture-key");
            using var provider = services.BuildServiceProvider();
            var reporter = new IncidentReporter(
                provider.GetRequiredService<AlertCenterApiClient>(),
                settings, restartedProcess, new ClickHouseLogReader(settings),
                NullLogger<IncidentReporter>.Instance);

            await reporter.ProcessTableAsync("TracerLogs", null, [], DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.IsNotNull(received);
            Assert.AreEqual(request.ReportId, received.ReportId);
            Assert.IsTrue(JToken.DeepEquals(request.Context, received.Context));
            Assert.IsTrue(JToken.DeepEquals(request.Metadata, received.Metadata));
            Assert.IsNull(restartedProcess.Pending("TracerLogs"));
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
