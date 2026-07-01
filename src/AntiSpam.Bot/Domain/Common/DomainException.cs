namespace AntiSpam.Bot.Domain.Common;

/// <summary>
/// Base for exceptions raised by the domain when a business rule is violated (invalid config value,
/// already-handled incident, ...). Pure domain: it carries only a human-readable message and knows
/// nothing about transport - the internal endpoints' DomainExceptionFilter is what turns it into the
/// "❌ {message}" reply the user sees.
/// </summary>
public abstract class DomainException(string message) : Exception(message);
