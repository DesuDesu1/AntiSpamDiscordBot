using System.Text.Json;
using AntiSpam.Bot.Infrastructure.Kafka;
using AntiSpam.Bot.Services.Discord;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;

namespace AntiSpam.Bot.Features.GuildManagement;

public class CommandConsumerWorker : BackgroundService
{
    private readonly ConsumerConfig _config;
    private readonly GuildCommandHandler _commandHandler;
    private readonly DiscordService _discord;
    private readonly ILogger<CommandConsumerWorker> _logger;

    public CommandConsumerWorker(
        ConsumerConfig config,
        GuildCommandHandler commandHandler,
        DiscordService discord,
        ILogger<CommandConsumerWorker> logger)
    {
        _config = config;
        _commandHandler = commandHandler;
        _discord = discord;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig(_config)
        {
            GroupId = "antispam-commands",
            AutoOffsetReset = AutoOffsetReset.Latest
        };

        using var consumer = new ConsumerBuilder<string, SlashCommandEvent>(config)
            .SetValueDeserializer(new SafeJsonDeserializer<SlashCommandEvent>())
            .Build();

        consumer.Subscribe(KafkaTopics.Commands);
        _logger.LogInformation("Subscribed to topic: {Topic}", KafkaTopics.Commands);

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(stoppingToken);
                if (result?.Message?.Value == null) continue;

                await ProcessCommandAsync(result.Message.Value);

                consumer.Commit(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error at {Topic}", KafkaTopics.Commands);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing command from Kafka");
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
        await _discord.SendFollowupAsync(token, message);
    }
}
