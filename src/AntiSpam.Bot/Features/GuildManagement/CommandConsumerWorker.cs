using System.Text.Json;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;
using Discord.Rest;

namespace AntiSpam.Bot.Features.GuildManagement;

public class CommandConsumerWorker : BackgroundService
{
    private readonly IConsumer<string, string> _consumer;
    private readonly GuildCommandHandler _commandHandler;
    private readonly DiscordRestClient _discord;
    private readonly HttpClient _http;
    private readonly ILogger<CommandConsumerWorker> _logger;

    public CommandConsumerWorker(
        IServiceProvider services,
        IConfiguration config,
        ILogger<CommandConsumerWorker> logger)
    {
        _commandHandler = services.GetRequiredService<GuildCommandHandler>();
        _discord = services.GetRequiredService<DiscordRestClient>();
        _http = new HttpClient();
        _logger = logger;

        var kafkaConfig = new ConsumerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"] ?? "localhost:9092",
            GroupId = "antispam-commands",
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };
        _consumer = new ConsumerBuilder<string, string>(kafkaConfig).Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(KafkaTopics.Commands);
        _logger.LogInformation("Subscribed to topic: {Topic}", KafkaTopics.Commands);

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = _consumer.Consume(TimeSpan.FromSeconds(1));
                if (result == null) continue;

                var command = JsonSerializer.Deserialize<SlashCommandEvent>(result.Message.Value);
                if (command == null) continue;

                await ProcessCommandAsync(command);

                _consumer.Commit(result);
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing command");
            }
        }
    }

    private async Task ProcessCommandAsync(SlashCommandEvent command)
    {
        _logger.LogInformation("Processing command /{Command} {SubCommand} from {User}", 
            command.CommandName, command.SubCommandName, command.Username);

        try
        {
            // Convert options from JsonElement to proper types
            var options = new Dictionary<string, object>();
            foreach (var kvp in command.Options)
            {
                if (kvp.Value is JsonElement element)
                {
                    options[kvp.Key] = element.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                        JsonValueKind.String => element.GetString()!,
                        _ => kvp.Value
                    };
                }
                else
                {
                    options[kvp.Key] = kvp.Value;
                }
            }

            // Handle command
            var response = await _commandHandler.HandleCommandAsync(
                command.GuildId, 
                command.SubCommandName, 
                options);

            // Respond via webhook (followup to deferred interaction)
            await SendFollowupAsync(command.InteractionToken, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process command");
            
            try
            {
                await SendFollowupAsync(command.InteractionToken, "❌ An error occurred processing your command.");
            }
            catch { /* ignore */ }
        }
    }

    private async Task SendFollowupAsync(string token, string message)
    {
        var applicationId = _discord.CurrentUser.Id;
        var url = $"https://discord.com/api/v10/webhooks/{applicationId}/{token}";
        
        var payload = JsonSerializer.Serialize(new 
        { 
            content = message, 
            flags = 64, // ephemeral
            allowed_mentions = new { parse = new[] { "users", "roles", "everyone" } }
        });
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        
        var response = await _http.PostAsync(url, content);
        
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to send followup: {Status} - {Body}", response.StatusCode, body);
        }
    }

    public override void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
        _http.Dispose();
        base.Dispose();
    }
}
