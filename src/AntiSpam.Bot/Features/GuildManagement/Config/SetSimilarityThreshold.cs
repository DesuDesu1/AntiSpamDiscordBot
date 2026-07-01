using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Infrastructure.Cache;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Config;

public sealed record SetSimilarityThresholdCommand(ulong GuildId, int Percent) : ICommand<string>;

public sealed class SetSimilarityThresholdHandler(BotDbContext db, GuildConfigCache cache)
    : ICommandHandler<SetSimilarityThresholdCommand, string>
{
    public async ValueTask<string> Handle(SetSimilarityThresholdCommand command, CancellationToken ct)
    {
        var config = await db.GuildConfigs.GetOrCreateAsync(command.GuildId, ct);

        config.SetSimilarityThreshold(command.Percent);
        await db.SaveChangesAsync(ct);
        await cache.InvalidateAsync(command.GuildId);
        return $"✅ Similarity threshold: {command.Percent}%";
    }
}

public sealed class SetSimilarityThresholdEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/similarity",
            async (SetSimilarityThresholdCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
