using AntiSpam.Bot.Common;
using Mediator;

namespace AntiSpam.Bot.Features.GuildManagement.Info;

public sealed record GetHelpCommand : ICommand<string>;

public sealed class GetHelpHandler : ICommandHandler<GetHelpCommand, string>
{
    private const string HelpText = """
        **Anti-Spam Bot**

        I automatically catch two kinds of spam and can delete the messages, time the user out, and alert your moderators:
        - The same or near-identical message posted across several channels in a short window.
        - Brand-new members posting links before they have any history (common bot behaviour).

        **Getting started**
        1. `/antispam alert-channel` - pick a moderators-only channel for alerts.
        2. `/antispam status` - review the current settings (every server starts with sensible defaults).
        3. Tune anything below to taste.

        **Detection**
        `/antispam min-channels` - how many channels the same message must hit (2-10)
        `/antispam similarity` - how alike messages must be to count as spam (50-100%)
        `/antispam window` - time window for the check, in seconds (30-600)
        `/antispam new-user-threshold` - how long someone counts as "new" for the link check (1-168h)

        **Allowed links** (links new members may post freely)
        `/antispam list-links`, `/antispam allow-link`, `/antispam remove-link`
        Well-known sites (youtube, twitch, etc.) are allowed by default.

        **Allowed server invites** (other servers new members may invite to)
        `/antispam list-invites`, `/antispam allow-invite`, `/antispam remove-invite`
        This server's own invites are always allowed; add others by their invite link.

        **Actions**
        `/antispam mute` - time the offender out (on/off, duration in minutes)
        `/antispam delete` - delete the detected spam (on/off)
        `/antispam link-detection` - turn the new-member link check on or off (leaves cross-channel spam detection on)
        `/antispam enable` - turn all protection on or off

        **Permissions I need**
        Grant my role: Moderate Members (timeout), Ban Members (the Ban button), Manage Messages (delete spam), and in the alert channel View Channel, Send Messages, Embed Links and Attach Files (to show the spam image).
        Drag my role high in the role list: Discord will not let me mute, ban, or delete messages for the server owner or anyone whose top role sits above mine - those actions silently fail.

        Using the `/antispam` commands themselves requires the **Manage Server** permission.

        **Privacy**
        Privacy policy: <https://github.com/DesuDesu1/AntiSpamDiscordBot/blob/main/PRIVACY.md>
        Flagged-spam records are auto-deleted after 7 days. For data-deletion requests or questions, contact @nanashi1725 on Discord or ddesuone@gmail.com.
        """;

    public ValueTask<string> Handle(GetHelpCommand command, CancellationToken ct) => new(HelpText);
}

public sealed class GetHelpEndpoint : IEndpointMapper
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/commands/help",
            async (GetHelpCommand cmd, IMediator mediator, CancellationToken ct) =>
                Results.Text(await mediator.Send(cmd, ct)));
        return app;
    }
}
