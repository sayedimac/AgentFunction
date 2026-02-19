using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace src;

public class GetWeather
{
    private readonly ILogger<GetWeather> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public GetWeather(ILogger<GetWeather> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    [Function("getWeather")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        // Read location from query string or JSON body
        string? location = req.Query["location"];

        if (string.IsNullOrWhiteSpace(location) && req.ContentLength > 0)
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            try
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("location", out var loc))
                {
                    location = loc.GetString();
                }
            }
            catch (JsonException)
            {
                // Body is not JSON — ignore
            }
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            return new BadRequestObjectResult(new
            {
                error = "Missing required parameter 'location'. Pass it as a query string or JSON body."
            });
        }

        var apiKey = Environment.GetEnvironmentVariable("OS_DATA_HUB_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith('<'))
        {
            _logger.LogError("OS_DATA_HUB_API_KEY is not configured.");
            return new ObjectResult(new { error = "Server configuration error: API key not set." })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }

        _logger.LogInformation("Looking up location '{Location}' via OS Data Hub.", location);

        try
        {
            var client = _httpClientFactory.CreateClient("OsDataHub");
            var requestUrl = $"search/names/v1/find?query={Uri.EscapeDataString(location)}&key={apiKey}";
            var response = await client.GetAsync(requestUrl);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("OS Data Hub returned {StatusCode}: {Body}",
                    (int)response.StatusCode, errorBody);

                return new ObjectResult(new
                {
                    error = $"OS Data Hub API returned {(int)response.StatusCode}.",
                    detail = errorBody
                })
                {
                    StatusCode = (int)response.StatusCode
                };
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);

            return new OkObjectResult(new
            {
                location,
                source = "OS Data Hub – Ordnance Survey",
                data = result.RootElement
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach OS Data Hub API.");
            return new ObjectResult(new { error = "Unable to reach OS Data Hub API.", detail = ex.Message })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
    }
}
