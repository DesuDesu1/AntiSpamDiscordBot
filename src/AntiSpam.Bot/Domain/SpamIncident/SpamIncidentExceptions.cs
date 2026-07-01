using AntiSpam.Bot.Domain.Common;

namespace AntiSpam.Bot.Domain.SpamIncident;

public sealed class IncidentAlreadyHandledException(string message) : DomainException(message);
