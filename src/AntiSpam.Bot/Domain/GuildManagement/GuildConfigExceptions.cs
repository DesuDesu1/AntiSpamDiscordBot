using AntiSpam.Bot.Domain.Common;

namespace AntiSpam.Bot.Domain.GuildManagement;

/// <summary>A config value was outside the range the domain allows.</summary>
public sealed class InvalidConfigValueException(string message) : DomainException(message);

public sealed class LinkAlreadyAllowedException(string message) : DomainException(message);

public sealed class AllowedLinkLimitExceededException(string message) : DomainException(message);

public sealed class LinkNotAllowedException(string message) : DomainException(message);

public sealed class InviteAlreadyAllowedException(string message) : DomainException(message);

public sealed class InviteNotAllowedException(string message) : DomainException(message);
