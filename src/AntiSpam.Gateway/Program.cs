using System.Text.Json;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;
using Discord;
using Discord.WebSocket;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<DiscordGatewayWorker>();
builder.Services.AddSingleton<DiscordSocketClient>(_ =>
{
    var config = new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds 
            | GatewayIntents.GuildMessages 
            | GatewayIntents.MessageContent
            | GatewayIntents.GuildMessageReactions
    };
    return new DiscordSocketClient(config);
});
builder.Services.AddSingleton<IProducer<string, string>>(sp =>
{
    var config = new ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
        Acks = Acks.All,
        EnableIdempotence = true
    };
    return new ProducerBuilder<string, string>(config).Build();
});

var host = builder.Build();
host.Run();

public class DiscordGatewayWorker : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly IProducer<string, string> _producer;
    private readonly IConfiguration _config;
    private readonly ILogger<DiscordGatewayWorker> _logger;

    public DiscordGatewayWorker(
        DiscordSocketClient client,
        IProducer<string, string> producer,
        IConfiguration config,
        ILogger<DiscordGatewayWorker> logger)
    {
        _client = client;
        _producer = producer;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.Log += LogAsync;
        _client.Ready += OnReadyAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.ButtonExecuted += OnButtonExecutedAsync;
        _client.ReactionAdded += OnReactionAddedAsync;
        _client.SlashCommandExecuted += OnSlashCommandExecutedAsync;

        var token = _config["Discord:Token"] 
            ?? throw new InvalidOperationException("Discord:Token not configured");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        _logger.LogInformation("Discord Gateway started");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnReadyAsync()
    {
        _logger.LogInformation("Bot is ready! Registering slash commands...");
        
        var antispamCommand = new SlashCommandBuilder()
            .WithName("antispam")
            .WithDescription("Anti-spam bot configuration")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("status")
                .WithDescription("Show current anti-spam settings")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("enable")
                .WithDescription("Enable or disable anti-spam protection")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("enabled", ApplicationCommandOptionType.Boolean, "Enable protection", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("alert-channel")
                .WithDescription("Set the channel for spam alerts")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("channel", ApplicationCommandOptionType.Channel, "Alert channel", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("min-channels")
                .WithDescription("Minimum channels for spam detection (2-10)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("count", ApplicationCommandOptionType.Integer, "Number of channels", isRequired: true, minValue: 2, maxValue: 10))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("similarity")
                .WithDescription("Text similarity threshold (50-100%)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("percent", ApplicationCommandOptionType.Integer, "Similarity percentage", isRequired: true, minValue: 50, maxValue: 100))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("window")
                .WithDescription("Detection time window in seconds (30-600)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("seconds", ApplicationCommandOptionType.Integer, "Time window", isRequired: true, minValue: 30, maxValue: 600))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("mute")
                .WithDescription("Configure mute on spam detection")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("enabled", ApplicationCommandOptionType.Boolean, "Enable mute", isRequired: true)
                .AddOption("duration", ApplicationCommandOptionType.Integer, "Duration in minutes (1-1440)", isRequired: false, minValue: 1, maxValue: 1440))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("delete")
                .WithDescription("Configure automatic message deletion")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("enabled", ApplicationCommandOptionType.Boolean, "Enable deletion", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("allow-link")
                .WithDescription("Add a link/domain to the allowed list for new users")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("link", ApplicationCommandOptionType.String, "Link or domain to allow (e.g. youtube.com or twitch.tv/mychannel)", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove-link")
                .WithDescription("Remove a link/domain from the allowed list")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("link", ApplicationCommandOptionType.String, "Link or domain to remove", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list-links")
                .WithDescription("List all allowed links/domains")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("new-user-threshold")
                .WithDescription("Set how long a user is considered 'new'")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("hours", ApplicationCommandOptionType.Integer, "Hours (1-168, default 24)", isRequired: true, minValue: 1, maxValue: 168))
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild);

        try
        {
            var testGuildId = _config["Discord:TestGuildId"];
            if (!string.IsNullOrEmpty(testGuildId))
            {
                var guild = _client.GetGuild(ulong.Parse(testGuildId));
                if (guild != null)
                {
                    await guild.CreateApplicationCommandAsync(antispamCommand.Build());
                    _logger.LogInformation("Registered slash commands to test guild {GuildId}", testGuildId);
                }
            }
            else
            {
                await _client.CreateGlobalApplicationCommandAsync(antispamCommand.Build());
                _logger.LogInformation("Registered global slash commands");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register slash commands");
        }
    }

    /// <summary>
    /// <summary>
    /// Convert Discord option value to a JSON-serializable primitive.
    /// ISnowflakeEntity is base for all Discord entities (channels, users, roles, etc.)
    /// </summary>
    private static object ConvertOptionValue(object? value)
    {
        if (value is null) return "";
        if (value is ISnowflakeEntity entity) return entity.Id.ToString();
        if (value is bool or string or int or long or double or ulong or float) return value;
        return value.ToString() ?? "";
    }

    private async Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
    {
        if (command.CommandName != "antispam") return;
        if (command.GuildId == null) return;

        // Defer first to acknowledge the interaction
        await command.DeferAsync(ephemeral: true);

        // Forward to Kafka for processing by Bot
        var subCommand = command.Data.Options.First();
        
        // Convert ALL options to serializable primitives only
        var options = new Dictionary<string, object>();
        if (subCommand.Options != null)
        {
            foreach (var opt in subCommand.Options)
            {
                options[opt.Name] = ConvertOptionValue(opt.Value);
                _logger.LogDebug("Option {Name}: {Type} -> {Value}", 
                    opt.Name, opt.Value?.GetType().Name ?? "null", options[opt.Name]);
            }
        }

        var @event = new SlashCommandEvent
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            GuildId = command.GuildId.Value,
            ChannelId = command.Channel.Id,
            UserId = command.User.Id,
            Username = command.User.Username,
            CommandName = command.CommandName,
            SubCommandName = subCommand.Name,
            Options = options,
            InteractionId = command.Id,
            InteractionToken = command.Token
        };

        await PublishAsync(KafkaTopics.Commands, command.GuildId.Value.ToString(), @event);
        
        _logger.LogInformation("Slash command /{Command} {SubCommand} from {User} in guild {Guild}", 
            command.CommandName, subCommand.Name, command.User.Username, command.GuildId);
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message is not SocketUserMessage)
            return;

        if (message.Channel is not SocketGuildChannel guildChannel)
            return;

        var guildUser = message.Author as Discord.WebSocket.SocketGuildUser;
        
        var @event = new MessageReceivedEvent
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            GuildId = guildChannel.Guild.Id,
            ChannelId = message.Channel.Id,
            MessageId = message.Id,
            AuthorId = message.Author.Id,
            AuthorUsername = message.Author.Username,
            Content = message.Content,
            IsBot = message.Author.IsBot,
            AttachmentCount = message.Attachments.Count,
            AuthorJoinedAt = guildUser?.JoinedAt
        };

        await PublishAsync(KafkaTopics.Messages, message.Author.Id.ToString(), @event);
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.GuildId == null) return;
        
        await component.DeferAsync();
        
        var @event = new InteractionEvent
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            GuildId = component.GuildId.Value,
            ChannelId = component.Channel.Id,
            UserId = component.User.Id,
            Username = component.User.Username,
            Type = ModInteractionType.Button,
            CustomId = component.Data.CustomId,
            MessageId = component.Message.Id
        };

        await PublishAsync(KafkaTopics.Interactions, component.User.Id.ToString(), @event);
        
        _logger.LogInformation("Button {CustomId} clicked by {User}", 
            component.Data.CustomId, component.User.Username);
    }

    private async Task OnReactionAddedAsync(
        Cacheable<IUserMessage, ulong> message, 
        Cacheable<IMessageChannel, ulong> channel, 
        SocketReaction reaction)
    {
        if (reaction.UserId == _client.CurrentUser?.Id) return;
        if (channel.Value is not SocketGuildChannel guildChannel) return;

        var emote = reaction.Emote.Name;
        if (emote != "🔨" && emote != "✅") return;

        var @event = new InteractionEvent
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            GuildId = guildChannel.Guild.Id,
            ChannelId = channel.Id,
            UserId = reaction.UserId,
            Username = reaction.User.IsSpecified ? reaction.User.Value.Username : "Unknown",
            Type = ModInteractionType.Reaction,
            MessageId = message.Id,
            Emoji = emote
        };

        await PublishAsync(KafkaTopics.Interactions, reaction.UserId.ToString(), @event);
        
        _logger.LogInformation("Reaction {Emoji} added by user {UserId} on message {MessageId}", 
            emote, reaction.UserId, message.Id);
    }

    private async Task PublishAsync<T>(string topic, string key, T @event)
    {
        try
        {
            var json = JsonSerializer.Serialize(@event);
            await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = json });
            _logger.LogDebug("Published to {Topic}: {Key}", topic, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish to {Topic}", topic);
        }
    }

    private Task LogAsync(Discord.LogMessage log)
    {
        _logger.Log(
            log.Severity switch
            {
                LogSeverity.Critical => LogLevel.Critical,
                LogSeverity.Error => LogLevel.Error,
                LogSeverity.Warning => LogLevel.Warning,
                LogSeverity.Info => LogLevel.Information,
                _ => LogLevel.Debug
            },
            log.Exception,
            "{Source}: {Message}",
            log.Source,
            log.Message);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
