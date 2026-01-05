using AntiSpam.Bot.Data;
using AntiSpam.Bot.Features.SpamDetection;
using AntiSpam.Bot.Services.Cache;
using AntiSpam.Bot.Services.Discord;
using Confluent.Kafka;
using Discord;
using Discord.Rest;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// PostgreSQL + EF Core (factory for singleton services)
builder.Services.AddDbContextFactory<BotDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));

// Discord REST client
builder.Services.AddSingleton<DiscordRestClient>(sp =>
{
    var client = new DiscordRestClient();
    var token = builder.Configuration["Discord:Token"];
    client.LoginAsync(TokenType.Bot, token).GetAwaiter().GetResult();
    return client;
});

// Kafka consumer
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

// Worker
builder.Services.AddHostedService<MessageConsumerWorker>();

var host = builder.Build();

// Ensure DB is created
await using (var db = await host.Services.GetRequiredService<IDbContextFactory<BotDbContext>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
}

host.Run();
