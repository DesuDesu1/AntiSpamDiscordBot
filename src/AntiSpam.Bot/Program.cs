using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Features.Moderation;
using AntiSpam.Bot.Features.SpamDetection;
using AntiSpam.Bot.Infrastructure;
using AntiSpam.Bot.Infrastructure.Cache;
using AntiSpam.Bot.Infrastructure.Discord;
using Discord;
using Discord.Rest;
using Mediator;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

ContentCipher.Init(builder.Configuration["Encryption:Key"]
    ?? throw new InvalidOperationException("Encryption:Key is not configured"));

if (string.IsNullOrEmpty(builder.Configuration["Internal:ApiKey"]))
    throw new InvalidOperationException("Internal:ApiKey is not configured");

var pgConnectionString = builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrEmpty(pgConnectionString))
{
    var pgHost = builder.Configuration["Postgres:Host"] ?? "localhost";
    var pgDatabase = builder.Configuration["Postgres:Database"] ?? "antispam";
    var pgUsername = builder.Configuration["Postgres:Username"] ?? "antispam";
    var pgPassword = builder.Configuration["Postgres:Password"] ?? "";
    pgConnectionString = $"Host={pgHost};Database={pgDatabase};Username={pgUsername};Password={pgPassword}";
}

var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (string.IsNullOrEmpty(redisConnectionString))
{
    redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
}


builder.Services.AddDbContextFactory<BotDbContext>(options =>
    options.UseNpgsql(pgConnectionString));
builder.Services.AddScoped<BotDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<BotDbContext>>().CreateDbContext());

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisConnectionString));

builder.Services.AddSingleton<DiscordRestClient>(sp =>
{
    var client = new DiscordRestClient();
    var token = builder.Configuration["Discord:Token"];
    client.LoginAsync(TokenType.Bot, token).GetAwaiter().GetResult();
    return client;
});

var kafkaServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
builder.Services.AddSingleton(new Confluent.Kafka.ConsumerConfig
{
    BootstrapServers = kafkaServers,
    GroupId = "antispam-bot-default",
    AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Latest,
    EnableAutoCommit = false
});

builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<GuildConfigCache>();

builder.Services.AddHttpClient(nameof(DiscordService))
    .AddStandardResilienceHandler();
builder.Services.AddSingleton<DiscordService>();

builder.Services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

builder.Services.AddProblemDetails();

builder.Services.AddHostedService<MessageConsumerWorker>();
builder.Services.AddHostedService<IncidentCleanupWorker>();

var app = builder.Build();

app.UseExceptionHandler();

app.MapGroup("/internal")
    .AddEndpointFilter<InternalApiKeyFilter>()
    .AddEndpointFilter<DomainExceptionFilter>()
    .MapAllEndpoints();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<BotDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

app.Run();
