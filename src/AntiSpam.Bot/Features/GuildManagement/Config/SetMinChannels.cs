using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Config;

public sealed record SetMinChannelsCommand(ulong GuildId, int Count) : ICommand<string>;

public sealed class SetMinChannelsHandler(BotDbContext db, GuildConfigCache cache)
    : ICommandHandler<SetMinChannelsCommand, string>
{
    public async ValueTask<string> Handle(SetMinChannelsCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        config.SetMinChannels(command.Count);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return $"✅ Minimum channels: {command.Count}";
    }
}

public sealed class SetMinChannelsEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/min-channels",
            async (SetMinChannelsCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
