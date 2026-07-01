using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Config;

public sealed record SetDetectionWindowCommand(ulong GuildId, int Seconds) : ICommand<string>;

public sealed class SetDetectionWindowHandler(BotDbContext db, GuildConfigCache cache)
    : ICommandHandler<SetDetectionWindowCommand, string>
{
    public async ValueTask<string> Handle(SetDetectionWindowCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        config.SetDetectionWindow(command.Seconds);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return $"✅ Detection window: {command.Seconds}s";
    }
}

public sealed class SetDetectionWindowEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/window",
            async (SetDetectionWindowCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
