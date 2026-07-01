using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Config;

public sealed record SetLinkDetectionCommand(ulong GuildId, bool Enabled) : ICommand<string>;

public sealed class SetLinkDetectionHandler(BotDbContext db, GuildConfigCache cache)
    : ICommandHandler<SetLinkDetectionCommand, string>
{
    public async ValueTask<string> Handle(SetLinkDetectionCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);
        config.SetNewUserLinkDetection(command.Enabled);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return command.Enabled
            ? "✅ New-member link detection enabled"
            : "❌ New-member link detection disabled (cross-channel spam detection still active)";
    }
}

public sealed class SetLinkDetectionEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/link-detection",
            async (SetLinkDetectionCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
