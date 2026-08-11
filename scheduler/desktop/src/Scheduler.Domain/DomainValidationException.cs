namespace Scheduler.Domain;

public sealed class DomainValidationException : ArgumentException
{
    public DomainValidationException(string message)
        : base(message)
    {
    }
}
