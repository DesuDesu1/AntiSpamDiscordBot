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
using ZiggyCreatures.Caching.Fusion;

var builder = WebApplication.CreateBuilder(args);

ContentCipher.Init(builder.Configuration["Encryption:Key"]
    ?? throw new InvalidOperationException("Encryption:Key is not configured"));

if (string.IsNullOrEmpty(builder.Configuration["Internal:ApiKey"]))
    throw new InvalidOperationException("Internal:ApiKey is not configured");

var pgConnectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured");

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured");

builder.Services.AddDbContextFactory<BotDbContext>(options =>
    options.UseNpgsql(pgConnectionString));
builder.Services.AddScoped<BotDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<BotDbContext>>().CreateDbContext());

var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

var discordToken = builder.Configuration["Discord:Token"]
                   ?? throw new InvalidOperationException("Discord:Token is not configured");

var discordClient = new DiscordRestClient();
await discordClient.LoginAsync(TokenType.Bot, discordToken);
builder.Services.AddSingleton(discordClient);

builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);
builder.Services.AddFusionCache()
    .WithSystemTextJsonSerializer()
    .WithRegisteredDistributedCache()
    .WithBackplane(new ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis.RedisBackplane(
        Microsoft.Extensions.Options.Options.Create(
            new ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis.RedisBackplaneOptions
            {
                Configuration = redisConnectionString
            })));

builder.Services.AddSingleton<MessageRepository>();

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
