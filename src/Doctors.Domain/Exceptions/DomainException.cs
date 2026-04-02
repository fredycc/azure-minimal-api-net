namespace Doctors.Domain.Exceptions;

/// <summary>
/// Base exception for domain-level errors.
/// </summary>
public class DomainException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DomainException"/> class with a message.
    /// </summary>
    public DomainException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainException"/> class with a message and inner exception.
    /// </summary>
    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}
