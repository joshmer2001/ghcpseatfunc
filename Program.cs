using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Components.Web;
using ghcpfunc;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DotNetEnv;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights isn't enabled by default. See https://aka.ms/AAt8mw4.
// builder.Services
//     .AddApplicationInsightsTelemetryWorkerService()
//     .ConfigureFunctionsApplicationInsights();

// Build the application
var host = builder.Build();

// Retrieve values from environment variables
// string key = Environment.GetEnvironmentVariable("GITHUB_API_KEY") ?? string.Empty;
// string enterprise = Environment.GetEnvironmentVariable("enterprise") ?? string.Empty; // Match case with .env file
// double days = double.TryParse(Environment.GetEnvironmentVariable("days"), out var parsedDays) ? parsedDays : 30; // Match case with .env file


// Run the application
await host.RunAsync();

builder.Build().Run();

public static class GitHubHelper
{
    public static async Task<(List<(string Username, DateTime LastActivity, string externalId)> InactiveUsers, List<(string Username, DateTime LastActivity, string externalId)> WarnUsers)> GetInactiveUsers(string key, string enterprise, double daysRemove, double daysWarn, ILogger logger)
    {
        List<(string Username, DateTime LastActivity, string externalId)> inactiveUserList = new List<(string Username, DateTime LastActivity, string externalId)>();
        List<(string Username, DateTime LastActivity, string externalId)> warnUserList = new List<(string Username, DateTime LastActivity, string externalId)>();
        
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key); // Corrected
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "ghcpfunc"); // Added User-Agent header

        var url = $"https://api.github.com/enterprises/{enterprise}/copilot/billing/seats"; // Changed to HTTPS
        logger.LogInformation("Sending request to GitHub API: {Url}", url);

        var response = await httpClient.GetAsync(url);
        logger.LogInformation("Received response from GitHub API: {StatusCode}", response.StatusCode);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("GitHub API request succeeded with status code: {StatusCode}", response.StatusCode);

            var content = await response.Content.ReadAsStreamAsync();
            var responseData = await JsonSerializer.DeserializeAsync<Response>(content);

            if (responseData?.Seats != null)
            {
                foreach (var seat in responseData.Seats)
                {
                    if (seat.LastActivityAt.AddDays(daysRemove) < DateTime.UtcNow)
                    {
                        logger.LogInformation("Inactive user found: {Username}, Last Activity: {LastActivity}", seat.Assignee.Login, seat.LastActivityAt);
                        inactiveUserList.Add((seat.Assignee.Login, seat.LastActivityAt, seat.Assignee.Id.ToString()));
                    }
                    else if (seat.LastActivityAt.AddDays(daysWarn) < DateTime.UtcNow)
                    {
                        logger.LogInformation("Inactive user found: {Username}, Last Activity: {LastActivity}", seat.Assignee.Login, seat.LastActivityAt);
                        warnUserList.Add((seat.Assignee.Login, seat.LastActivityAt, seat.Assignee.Id.ToString()));
                    }
                }
            }
            else
            {
                logger.LogWarning("No seats data found in the response.");
            }
        }
        else
        {
            logger.LogError("GitHub API request failed with status code: {StatusCode}", response.StatusCode);
            throw new Exception($"Error: {response.StatusCode}");
        }

        return (InactiveUsers: inactiveUserList, WarnUsers: warnUserList);
    }
}
