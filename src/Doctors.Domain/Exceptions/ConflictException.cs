namespace Doctors.Domain.Exceptions;

/// <summary>
/// Exception thrown when a conflict occurs (e.g., duplicate license number).
/// </summary>
public class ConflictException : DomainException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConflictException"/> class with a conflict message.
    /// </summary>
    /// <param name="message">Description of the conflict.</param>
    public ConflictException(string message) : base(message) { }
}
