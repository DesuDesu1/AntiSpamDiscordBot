using AntiSpam.Bot.Data;
using AntiSpam.Bot.Features.Moderation;
using AntiSpam.Bot.Features.SpamDetection;
using AntiSpam.Bot.Services.Cache;
using AntiSpam.Bot.Services.Discord;
using Confluent.Kafka;
using Discord;
using Discord.Rest;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Build PostgreSQL connection string
var pgConnectionString = builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrEmpty(pgConnectionString))
{
    // Build from individual components (K8s environment)
    var pgHost = builder.Configuration["Postgres:Host"] ?? "localhost";
    var pgDatabase = builder.Configuration["Postgres:Database"] ?? "antispam";
    var pgUsername = builder.Configuration["Postgres:Username"] ?? "antispam";
    var pgPassword = builder.Configuration["Postgres:Password"] ?? "";
    pgConnectionString = $"Host={pgHost};Database={pgDatabase};Username={pgUsername};Password={pgPassword}";
}

// Build Redis connection string
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (string.IsNullOrEmpty(redisConnectionString))
{
    redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
}

// PostgreSQL + EF Core (factory for singleton services)
builder.Services.AddDbContextFactory<BotDbContext>(options =>
    options.UseNpgsql(pgConnectionString));

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisConnectionString));

// Discord REST client
builder.Services.AddSingleton<DiscordRestClient>(sp =>
{
    var client = new DiscordRestClient();
    var token = builder.Configuration["Discord:Token"];
    client.LoginAsync(TokenType.Bot, token).GetAwaiter().GetResult();
    return client;
});

// Kafka consumer for messages
builder.Services.AddSingleton<IConsumer<string, string>>(_ =>
{
    var config = new ConsumerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"],
        GroupId = "antispam-bot",
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoCommit = false
    };
    return new ConsumerBuilder<string, string>(config).Build();
});

// Cache
builder.Services.AddSingleton<MessageRepository>();

// Spam detection
builder.Services.AddSingleton<SpamDetector>();

// Discord actions
builder.Services.AddSingleton<DiscordService>();
builder.Services.AddSingleton<SpamActionService>();

// Workers
builder.Services.AddHostedService<MessageConsumerWorker>();
builder.Services.AddHostedService<InteractionConsumerWorker>();

var app = builder.Build();

// Ensure DB is created
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<BotDbContext>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
}

app.Run();
