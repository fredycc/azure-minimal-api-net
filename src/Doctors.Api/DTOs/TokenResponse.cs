namespace Doctors.Api.DTOs;

public record TokenResponse(string Token, DateTime ExpiresAt);