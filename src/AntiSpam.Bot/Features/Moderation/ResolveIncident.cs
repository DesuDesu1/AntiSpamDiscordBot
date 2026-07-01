using System.Text.RegularExpressions;
using AntiSpam.Bot.Common;
using AntiSpam.Bot.Data;
using AntiSpam.Bot.Domain.SpamIncident;
using AntiSpam.Bot.Infrastructure.Cache;
using AntiSpam.Bot.Infrastructure.Discord;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.Moderation;

/// <summary>
/// Bans or releases a pending incident. Whether the id came from a button's custom_id or from
/// looking up the alert message a reaction landed on is the trigger's concern (see the endpoints
/// in Interactions.cs) - by the time this command runs it's just an incident id. The Pending -&gt;
/// Banned/Released transition is enforced by <see cref="SpamIncident.Resolve"/>, which throws
/// <see cref="IncidentAlreadyHandledException"/> on a double-handle.
/// </summary>
public sealed record ResolveIncidentCommand(long IncidentId, ulong ModeratorId, string ModeratorName, IncidentOutcome Outcome) : ICommand;

public sealed class ResolveIncidentHandler : ICommandHandler<ResolveIncidentCommand>
{
    private readonly BotDbContext _db;
    private readonly MessageRepository _messages;
    private readonly DiscordService _discord;
    private readonly ILogger<ResolveIncidentHandler> _logger;

    public ResolveIncidentHandler(BotDbContext db,
        MessageRepository messages,
        DiscordService discord,
        ILogger<ResolveIncidentHandler> logger)
    {
        _db = db;
        _messages = messages;
        _discord = discord;
        _logger = logger;
    }

    public async ValueTask<Unit> Handle(ResolveIncidentCommand command, CancellationToken ct)
    {
        var incident = await _db.SpamIncidents.FindAsync([command.IncidentId], ct);
        if (incident == null)
        {
            _logger.LogWarning("Incident #{Id} not found", command.IncidentId);
            return Unit.Value;
        }

        incident.Resolve(command.ModeratorId, command.ModeratorName, command.Outcome);

        var actionFailed = false;
        if (command.Outcome == IncidentOutcome.Ban)
        {
            actionFailed = !await _discord.BanUserAsync(incident.GuildId, incident.UserId, "Spam detected");
        }
        else
        {
            await _discord.UnmuteUserAsync(incident.GuildId, incident.UserId);
        }

        // Give the user a clean slate: a released user must not be re-flagged by the leftover
        // window, and a renewed burst can raise a fresh alert.
        await _messages.ResetSpamStateAsync(incident.GuildId, incident.UserId);

        await _db.SaveChangesAsync(ct);

        var action = command.Outcome == IncidentOutcome.Ban ? "Banned" : "Released";
        await _discord.UpdateAlertMessageAsync(incident.GuildId, incident, action, command.ModeratorName, actionFailed);

        _logger.LogInformation("Incident #{Id} {Action} by {Moderator} (failed={Failed})",
            command.IncidentId, action, command.ModeratorName, actionFailed);

        return Unit.Value;
    }
}
/// <summary>A moderator clicked Ban/Release on a spam alert's buttons.</summary>
public sealed record ButtonInteractionRequest(ulong UserId, string Username, string CustomId);

/// <summary>A moderator reacted 🔨/✅ on a spam alert message in the mod channel.</summary>
public sealed record ReactionInteractionRequest(ulong UserId, string Username, ulong MessageId, string Emoji);

public sealed partial class InteractionEndpoints : IEndpointMapper
{
    [GeneratedRegex(@"spam_(ban|release)_(\d+)")]
    private static partial Regex ButtonIdRegex();

    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/interactions/button", async (
            ButtonInteractionRequest request, IMediator mediator, CancellationToken ct) =>
        {
            var match = ButtonIdRegex().Match(request.CustomId);
            if (!match.Success)
                return Results.Ok();

            var outcome = match.Groups[1].Value == "ban" ? IncidentOutcome.Ban : IncidentOutcome.Release;
            var incidentId = long.Parse(match.Groups[2].Value);
            await mediator.Send(new ResolveIncidentCommand(incidentId, request.UserId, request.Username, outcome), ct);
            return Results.Ok();
        });

        app.MapPost("/interactions/reaction", async (
            ReactionInteractionRequest request, IMediator mediator, BotDbContext db, CancellationToken ct) =>
        {
            var incident = await db.SpamIncidents.AsNoTracking()
                .FirstOrDefaultAsync(i => i.AlertMessageId == request.MessageId, ct);
            if (incident == null)
                return Results.Ok();

            var outcome = request.Emoji == "🔨" ? IncidentOutcome.Ban : IncidentOutcome.Release;
            await mediator.Send(new ResolveIncidentCommand(incident.Id, request.UserId, request.Username, outcome), ct);
            return Results.Ok();
        });

        return app;
    }
}
