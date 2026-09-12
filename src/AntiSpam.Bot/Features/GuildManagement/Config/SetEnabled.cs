using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;
using ZiggyCreatures.Caching.Fusion;

namespace AntiSpam.Bot.Features.GuildManagement.Config;

public sealed record SetEnabledCommand(ulong GuildId, bool Enabled) : ICommand<string>;

public sealed class SetEnabledHandler(BotDbContext db, IFusionCache cache)
    : ICommandHandler<SetEnabledCommand, string>
{
    public async ValueTask<string> Handle(SetEnabledCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        if (command.Enabled) config.Enable();
        else config.Disable();

        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.GuildConfig(command.GuildId));
        return command.Enabled ? "✅ Protection enabled" : "❌ Protection disabled";
    }
}

public sealed class SetEnabledEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/enable",
            async (SetEnabledCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
