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

// Initialise at-rest encryption for stored message content (Discord dev policy).
ContentCipher.Init(builder.Configuration["Encryption:Key"]
    ?? throw new InvalidOperationException("Encryption:Key is not configured"));

// Fail fast rather than have /internal/commands and /internal/interactions silently 401 forever.
if (string.IsNullOrEmpty(builder.Configuration["Internal:ApiKey"]))
    throw new InvalidOperationException("Internal:ApiKey is not configured");

// Build PostgreSQL connection string
var pgConnectionString = builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrEmpty(pgConnectionString))
{
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

// PostgreSQL + EF Core. The factory feeds the singleton DiscordService (which opens its own
// short-lived context); handlers get a plain scoped BotDbContext built from that same factory, so
// they inject the context directly instead of juggling a factory in every slice.
builder.Services.AddDbContextFactory<BotDbContext>(options =>
    options.UseNpgsql(pgConnectionString));
builder.Services.AddScoped<BotDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<BotDbContext>>().CreateDbContext());

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

// Message stream is still Kafka-backed (see MessageConsumerWorker); everything else moved to HTTP.
var kafkaServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
builder.Services.AddSingleton(new Confluent.Kafka.ConsumerConfig
{
    BootstrapServers = kafkaServers,
    GroupId = "antispam-bot-default",
    AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,
    EnableAutoCommit = false
});

// Redis-backed caches. Singleton is fine: they only hold an IConnectionMultiplexer, no DbContext.
builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<GuildConfigCache>();

// Discord actions (use HttpClientFactory + Resilience)
builder.Services.AddHttpClient(nameof(DiscordService))
    .AddStandardResilienceHandler();
builder.Services.AddSingleton<DiscordService>();

// Mediator (scoped so handlers can inject the scoped BotDbContext) + cross-cutting pipeline logging.
builder.Services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

// DomainExceptions are translated to the user-facing "❌ ..." reply by DomainExceptionFilter on the
// /internal group; ProblemDetails here just gives unexpected (non-domain) exceptions a structured 500.
builder.Services.AddProblemDetails();

// Workers
builder.Services.AddHostedService<MessageConsumerWorker>();
builder.Services.AddHostedService<IncidentCleanupWorker>();

var app = builder.Build();

app.UseExceptionHandler();

// All Gateway -> Bot endpoints live under /internal. Both cross-cutting concerns are declared once
// on the group instead of per slice: the shared-secret check, then domain-error -> "❌ ..." reply.
// Each slice's IEndpointMapper maps a relative route.
app.MapGroup("/internal")
    .AddEndpointFilter<InternalApiKeyFilter>()
    .AddEndpointFilter<DomainExceptionFilter>()
    .MapAllEndpoints();

// Apply pending migrations
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<BotDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

app.Run();
