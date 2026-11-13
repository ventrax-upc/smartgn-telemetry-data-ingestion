using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Smartgn.Telemetry.Configuration;

public static class RemoteServiceBootstrap
{
    public static async Task LoadConfigServerAsync(IConfigurationManager configuration)
    {
        var configUrl = Environment.GetEnvironmentVariable("SMARTGN_CONFIG_URL");
        if (string.IsNullOrWhiteSpace(configUrl))
        {
            return;
        }

        var application = Environment.GetEnvironmentVariable("SMARTGN_CONFIG_APPLICATION") ?? "smartgn-telemetry";
        var profile = Environment.GetEnvironmentVariable("SMARTGN_CONFIG_PROFILE") ?? "local";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync($"{configUrl.TrimEnd('/')}/{application}/{profile}");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var sources = document.RootElement.GetProperty("propertySources");
        if (sources.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Config Server has no property source for {application}/{profile}.");
        }
        foreach (var propertySource in sources.EnumerateArray().Reverse())
        {
            foreach (var property in propertySource.GetProperty("source").EnumerateObject())
            {
                properties[property.Name.Replace('.', ':')] = property.Value.ToString();
            }
        }

        configuration.AddInMemoryCollection(properties);
        configuration.AddEnvironmentVariables();
    }
}

public sealed class AdminRegistrationWorker(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AdminRegistrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var adminUrl = configuration["SMARTGN_ADMIN_URL"];
        if (string.IsNullOrWhiteSpace(adminUrl))
        {
            return;
        }

        var serviceUrl = configuration["SMARTGN_SERVICE_URL"] ?? "http://telemetry:8085";
        var serviceName = configuration["SMARTGN_SERVICE_NAME"] ?? "smartgn-telemetry";
        var username = configuration["SMARTGN_ADMIN_USERNAME"];
        var password = configuration["SMARTGN_ADMIN_PASSWORD"];
        var client = httpClientFactory.CreateClient(nameof(AdminRegistrationWorker));
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var registration = new
                {
                    name = serviceName,
                    serviceUrl,
                    managementUrl = serviceUrl,
                    healthUrl = $"{serviceUrl.TrimEnd('/')}/ready",
                };
                using var response = await client.PostAsJsonAsync(
                    $"{adminUrl.TrimEnd('/')}/instances", registration, stoppingToken);
                response.EnsureSuccessStatusCode();
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                logger.LogInformation(exception, "Admin registration for {ServiceName} will retry", serviceName);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
