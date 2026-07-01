using AntiSpam.Bot.Domain.SpamIncident;

namespace AntiSpam.Tests;

public class SpamIncidentTests
{
    private static SpamIncident Raise() =>
        SpamIncident.Raise(1, 2, "user", "spam content", [10, 11]);

    [Fact]
    public void Raise_truncates_content_to_500_chars()
    {
        var incident = SpamIncident.Raise(1, 2, "user", new string('x', 600), [10]);
        Assert.Equal(500, incident.Content.Length);
    }

    [Fact]
    public void Raise_starts_pending()
    {
        Assert.Equal(IncidentStatus.Pending, Raise().Status);
    }

    [Fact]
    public void Resolve_ban_sets_status_and_moderator()
    {
        var incident = Raise();
        incident.Resolve(99, "mod", IncidentOutcome.Ban);

        Assert.Equal(IncidentStatus.Banned, incident.Status);
        Assert.Equal(99ul, incident.HandledByUserId);
        Assert.Equal("mod", incident.HandledByUsername);
        Assert.NotNull(incident.HandledAt);
    }

    [Fact]
    public void Resolve_release_sets_released()
    {
        var incident = Raise();
        incident.Resolve(99, "mod", IncidentOutcome.Release);
        Assert.Equal(IncidentStatus.Released, incident.Status);
    }

    [Fact]
    public void Resolve_twice_throws_IncidentAlreadyHandled()
    {
        var incident = Raise();
        incident.Resolve(99, "mod", IncidentOutcome.Ban);
        Assert.Throws<IncidentAlreadyHandledException>(() => incident.Resolve(100, "mod2", IncidentOutcome.Release));
    }

    [Fact]
    public void AttachAlert_records_where_the_alert_landed()
    {
        var incident = Raise();
        incident.AttachAlert(500, 600);
        Assert.Equal(500ul, incident.AlertChannelId);
        Assert.Equal(600ul, incident.AlertMessageId);
    }
}
