using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntiSpam.Contracts;
using AntiSpam.Contracts.Events;
using Confluent.Kafka;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

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

// Slash commands and moderation clicks go straight to Bot's internal API instead of Kafka -
// they're human-rate and the interaction is already deferred by the time we call this.
builder.Services.AddHttpClient("BotInternal", (sp, client) =>
    {
        var baseUrl = sp.GetRequiredService<IConfiguration>()["Internal:BotBaseUrl"] ?? "http://antispam-bot:8080";
        client.BaseAddress = new Uri(baseUrl);
        var apiKey = sp.GetRequiredService<IConfiguration>()["Internal:ApiKey"];
        if (!string.IsNullOrEmpty(apiKey))
            client.DefaultRequestHeaders.Add("X-Internal-Key", apiKey);
    })
    .AddStandardResilienceHandler();

var host = builder.Build();
host.Run();

public class DiscordGatewayWorker : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly IProducer<string, string> _producer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DiscordGatewayWorker> _logger;

    public DiscordGatewayWorker(
        DiscordSocketClient client,
        IProducer<string, string> producer,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<DiscordGatewayWorker> logger)
    {
        _client = client;
        _producer = producer;
        _httpClientFactory = httpClientFactory;
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
        _client.SlashCommandExecuted += OnDesubotCommandExecutedAsync;

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
                .WithName("help")
                .WithDescription("Explain what the bot does and how to set it up")
                .WithType(ApplicationCommandOptionType.SubCommand))
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
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("link-detection")
                .WithDescription("Turn new-member link detection on or off (a common false-positive source)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("enabled", ApplicationCommandOptionType.Boolean, "Flag new members who post external links", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("allow-invite")
                .WithDescription("Allow new members to post invites to another server")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("invite", ApplicationCommandOptionType.String, "Invite link to the server to allow (e.g. discord.gg/xyz)", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove-invite")
                .WithDescription("Remove a server from the invite allow-list")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("invite", ApplicationCommandOptionType.String, "Invite link, or the server id shown in list-invites", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list-invites")
                .WithDescription("List servers whose invites new members may post")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild);

        try
        {
            var testGuildId = _config["Discord:TestGuildId"];
            if (!string.IsNullOrEmpty(testGuildId))
            {
                var guild = _client.GetGuild(ulong.Parse(testGuildId));
                if (guild != null)
                {
                    // Bulk overwrite replaces the guild's whole command set, so any command
                    // that is no longer defined here (e.g. an old ghost command) gets removed.
                    var desubotCommand = BuildDesubotCommand();
                    await guild.BulkOverwriteApplicationCommandAsync([antispamCommand.Build(), desubotCommand]);
                    _logger.LogInformation("Registered slash commands to test guild {GuildId}", testGuildId);
                }
            }
            else
            {
                // Bulk overwrite replaces the application's whole global command set, so stale
                // registrations (e.g. a leftover /add-ban-word) are deleted rather than kept.
                var desubotCommand = BuildDesubotCommand();
                await _client.BulkOverwriteGlobalApplicationCommandsAsync([antispamCommand.Build(), desubotCommand]);
                _logger.LogInformation("Registered global slash commands");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register slash commands");
        }
    }

    private static ApplicationCommandProperties BuildDesubotCommand() =>
        new SlashCommandBuilder()
            .WithName("desubot")
            .WithDescription("Bot owner commands")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("set-avatar")
                .WithDescription("Set the bot's avatar (owner only)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("url", ApplicationCommandOptionType.String,
                    "Direct image/GIF URL, or omit to reset to the default desubot avatar",
                    isRequired: false))
            .Build();

    private async Task OnDesubotCommandExecutedAsync(SocketSlashCommand command)
    {
        if (command.CommandName != "desubot") return;

        var ownerIdStr = _config["Discord:OwnerId"];
        if (string.IsNullOrEmpty(ownerIdStr) || !ulong.TryParse(ownerIdStr, out var ownerId)
            || command.User.Id != ownerId)
        {
            await command.RespondAsync("Not authorized.", ephemeral: true);
            return;
        }

        var subCommand = command.Data.Options.First();
        if (subCommand.Name != "set-avatar") return;

        await command.DeferAsync(ephemeral: true);

        try
        {
            var urlOpt = subCommand.Options?.FirstOrDefault(o => o.Name == "url")?.Value as string;
            Stream imageStream;

            if (!string.IsNullOrEmpty(urlOpt))
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("desubot/1.0");
                imageStream = await http.GetStreamAsync(urlOpt);
            }
            else
            {
                // Default bundled avatar — resolved relative to the assembly location
                var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
                var gifPath = Path.Combine(dir, "desubot_avatar.gif");
                if (!File.Exists(gifPath))
                    gifPath = Path.Combine(AppContext.BaseDirectory, "desubot_avatar.gif");
                imageStream = File.OpenRead(gifPath);
            }

            await using (imageStream)
            {
                await _client.CurrentUser.ModifyAsync(props =>
                    props.Avatar = new Image(imageStream));
            }

            await command.FollowupAsync("Avatar updated!", ephemeral: true);
            _logger.LogInformation("Bot avatar updated by owner {UserId}", command.User.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set avatar");
            await command.FollowupAsync($"Failed: {ex.Message}", ephemeral: true);
        }
    }

    /// <summary>
    /// Converts a Discord option value to a JSON node for the request body. This is the transport
    /// boundary: Discord.Net hands us <see cref="object"/>, we turn it into a primitive JSON value
    /// (snowflake entities collapse to their id) so Bot can bind it to the typed command it owns.
    /// </summary>
    private static JsonNode? OptionValueToJson(object? value) => value switch
    {
        null => null,
        ISnowflakeEntity entity => JsonValue.Create(entity.Id),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(value.ToString())
    };

    private async Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
    {
        if (command.CommandName != "antispam") return;
        if (command.GuildId == null) return;

        // Acknowledge within the 3s deadline; we then have 15 min to follow up with Bot's reply.
        await command.DeferAsync(ephemeral: true);

        var subCommand = command.Data.Options.First();

        // Dispatch is the route: the subcommand name selects Bot's per-command endpoint. The body
        // is the guild plus each option under its own name - Bot binds it to the command DTO it owns,
        // so no command contract is shared across the two services.
        var body = new JsonObject { ["guildId"] = JsonValue.Create(command.GuildId.Value) };
        if (subCommand.Options != null)
        {
            foreach (var opt in subCommand.Options)
                body[opt.Name] = OptionValueToJson(opt.Value);
        }

        var response = await PostCommandAsync(subCommand.Name, body);
        await command.FollowupAsync(response ?? "❌ An error occurred processing your command.", ephemeral: true);

        _logger.LogInformation("Slash command /{Command} {SubCommand} from {User} in guild {Guild}",
            command.CommandName, subCommand.Name, command.User.Username, command.GuildId);
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message is not SocketUserMessage userMessage)
            return;

        if (message.Author.IsBot)
            return;

        if (message.Channel is not SocketGuildChannel guildChannel)
            return;

        var guildUser = message.Author as Discord.WebSocket.SocketGuildUser;
        
        _logger.LogInformation("Message from {User} in {Guild}/{Channel}: {ContentLen} chars", 
            message.Author.Username, guildChannel.Guild.Name, message.Channel.Name, 
            message.Content?.Length ?? 0);
        
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
            IsBot = false,
            AttachmentCount = message.Attachments.Count,
            AttachmentUrls = message.Attachments.Select(a => a.Url).ToList(),
            AuthorJoinedAt = guildUser?.JoinedAt
        };

        await PublishAsync(KafkaTopics.Messages, message.Author.Id.ToString(), @event);
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.GuildId == null) return;

        await component.DeferAsync();

        await PostInternalAsync("/internal/interactions/button", new
        {
            userId = component.User.Id,
            username = component.User.Username,
            customId = component.Data.CustomId
        });

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

        await PostInternalAsync("/internal/interactions/reaction", new
        {
            userId = reaction.UserId,
            username = reaction.User.IsSpecified ? reaction.User.Value.Username : "Unknown",
            messageId = message.Id,
            emoji = emote
        });

        _logger.LogInformation("Reaction {Emoji} added by user {UserId} on message {MessageId}",
            emote, reaction.UserId, message.Id);
    }

    /// <summary>Publishes to the one topic that's still Kafka: unbounded, bursty message volume.</summary>
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

    /// <summary>
    /// Posts a `/antispam` subcommand to Bot's matching endpoint and returns the reply text to show
    /// the user. Slash commands are human-rate and already deferred, so a direct call is fine - only
    /// the unbounded message stream needs Kafka. Returns null if Bot couldn't be reached.
    /// </summary>
    private async Task<string?> PostCommandAsync(string subCommand, JsonNode body)
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

    /// <summary>Fire-and-forget post of a moderation interaction (button/reaction) to Bot.</summary>
    private async Task PostInternalAsync<T>(string path, T payload)
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
