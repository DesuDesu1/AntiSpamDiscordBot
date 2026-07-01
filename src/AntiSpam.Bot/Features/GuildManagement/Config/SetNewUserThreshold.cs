using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Config;

public sealed record SetNewUserThresholdCommand(ulong GuildId, int Hours) : ICommand<string>;

public sealed class SetNewUserThresholdHandler(BotDbContext db, GuildConfigCache cache)
    : ICommandHandler<SetNewUserThresholdCommand, string>
{
    public async ValueTask<string> Handle(SetNewUserThresholdCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        config.SetNewUserThreshold(command.Hours);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return $"✅ New user threshold set to {command.Hours}h";
    }
}

public sealed class SetNewUserThresholdEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/new-user-threshold",
            async (SetNewUserThresholdCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
