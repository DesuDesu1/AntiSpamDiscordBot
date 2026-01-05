using AntiSpam.Bot.Features.SpamDetection;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Redis
var redisConnection = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnection));

// Services
builder.Services.AddSingleton<UserMessageCache>();

// Workers
builder.Services.AddHostedService<MessageConsumerWorker>();

var host = builder.Build();
host.Run();
