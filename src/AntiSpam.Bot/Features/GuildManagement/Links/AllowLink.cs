using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Links;

public sealed record AllowLinkCommand(ulong GuildId, string Link) : ICommand<string>;

public sealed class AllowLinkHandler(BotDbContext db, GuildConfigCache cache)
    : ICommandHandler<AllowLinkCommand, string>
{
    public async ValueTask<string> Handle(AllowLinkCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        // Throws (dup / cap / invalid) before we save, so nothing is persisted on rejection.
        var normalized = config.AllowLink(command.Link);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return $"✅ Added `{normalized}` to allowed links";
    }
}

public sealed class AllowLinkEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/allow-link",
            async (AllowLinkCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
