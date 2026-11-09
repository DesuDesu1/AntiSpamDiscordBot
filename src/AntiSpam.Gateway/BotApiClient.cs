using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AntiSpam.Gateway;

public sealed class BotApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BotApiClient> _logger;

    public BotApiClient(IHttpClientFactory httpClientFactory, ILogger<BotApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> PostCommandAsync(string subCommand, JsonNode body)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient("BotInternal");
            var response = await http.PostAsJsonAsync($"/internal/commands/{subCommand}", body);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bot command {SubCommand} returned {Status}", subCommand, response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Bot command endpoint {SubCommand}", subCommand);
            return null;
        }
    }

    public async Task PostInternalAsync<T>(string path, T payload)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient("BotInternal");
            var response = await http.PostAsJsonAsync(path, payload);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Bot internal call to {Path} returned {Status}", path, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Bot internal endpoint {Path}", path);
        }
    }
}
