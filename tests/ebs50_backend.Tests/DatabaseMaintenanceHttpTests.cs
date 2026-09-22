using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ebs50_backend.Controllers;
using ebs50_backend.DTOs;
using ebs50_backend.OpenApi;
using ebs50_backend.Services.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ebs50_backend.Tests;

public sealed class DatabaseMaintenanceHttpTests
{
    [Fact]
    public async Task RazorAndApi_RequireCsrf_ExportValidateRestoreAndResume_WorkOverHttp()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.SeedAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DatabaseController).Assembly.GetName().Name,
            EnvironmentName = "Development"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(fixture.Service);
        builder.Services.AddSingleton(fixture.Coordinator);
        // Keep test keys in memory; do not depend on or modify the Windows account's key ring.
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddControllers().AddApplicationPart(typeof(DatabaseController).Assembly);
        builder.Services.AddRazorPages().AddApplicationPart(typeof(DatabaseController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options => options.OperationFilter<ApiContractOperationFilter>());
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<DatabaseMaintenanceMiddleware>();
        app.UseSwagger();
        app.MapControllers();
        app.MapRazorPages();
        app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" }));
        app.MapGet("/ordinary-data", () => Results.Ok());
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var handler = new HttpClientHandler { UseProxy = false, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(address) };
        try
        {
            using var pageResponse = await client.GetAsync("/Database");
            var page = await pageResponse.Content.ReadAsStringAsync();
            Assert.True(pageResponse.IsSuccessStatusCode, page);
            Assert.Contains("database-export", page);
            var match = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(match.Success, page);
            using var noCsrf = await client.PostAsync("/api/database/exports", null);
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", WebUtility.HtmlDecode(match.Groups[1].Value));

            using var exportResponse = await client.PostAsync("/api/database/exports", null);
            Assert.Equal(HttpStatusCode.Accepted, exportResponse.StatusCode);
            var export = (await exportResponse.Content.ReadFromJsonAsync<DatabaseOperation>())!;
            Assert.Contains(export.Id.ToString(), exportResponse.Headers.Location!.ToString());
            Assert.Equal("Succeeded", (await fixture.CompleteAsync(export)).Status);
            using var download = await client.GetAsync($"/api/database/exports/{export.Id}/download");
            Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
            using var upload = new MultipartFormDataContent();
            upload.Add(new ByteArrayContent(await download.Content.ReadAsByteArrayAsync()), "file", "backup.zip");
            using var validationResponse = await client.PostAsync("/api/database/imports/validate", upload);
            Assert.Equal(HttpStatusCode.Accepted, validationResponse.StatusCode);
            var validation = (await validationResponse.Content.ReadFromJsonAsync<DatabaseOperation>())!;
            Assert.Equal("Succeeded", (await fixture.CompleteAsync(validation)).Status);
            using var restoreResponse = await client.PostAsync($"/api/database/imports/{validation.Id}/restore", null);
            Assert.Equal(HttpStatusCode.Accepted, restoreResponse.StatusCode);
            var restore = (await restoreResponse.Content.ReadFromJsonAsync<DatabaseOperation>())!;
            Assert.Equal("Succeeded", (await fixture.CompleteAsync(restore)).Status);
            var status = await client.GetFromJsonAsync<DatabaseMaintenanceStatus>("/api/database/status");
            Assert.True(status!.SyncPaused);
            using var resumeResponse = await client.PostAsync("/api/database/resume-sync", null);
            Assert.Equal(HttpStatusCode.Accepted, resumeResponse.StatusCode);
            Assert.Equal("Succeeded", (await fixture.CompleteAsync((await resumeResponse.Content.ReadFromJsonAsync<DatabaseOperation>())!)).Status);

            using (var exclusive = await fixture.Coordinator.EnterMaintenanceAsync(TimeSpan.FromSeconds(1), default))
            {
                using var ordinary = await client.GetAsync("/ordinary-data");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, ordinary.StatusCode);
                using var health = await client.GetAsync("/health/live");
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                using var progress = await client.GetAsync($"/api/database/operations/{restore.Id}");
                Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
            }
            var swagger = await client.GetStringAsync("/swagger/v1/swagger.json");
            Assert.Contains("/api/database/imports/validate", swagger);
            Assert.Contains("multipart/form-data", swagger);
            client.DefaultRequestHeaders.Host = "attacker.example";
            using var forbidden = await client.GetAsync("/api/database/status");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
