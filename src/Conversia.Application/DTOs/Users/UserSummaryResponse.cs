using Conversia.Domain.Entities;

namespace Conversia.Application.DTOs.Users;

/// <summary>
/// One row of <c>GET /api/users</c> (users in the caller's own tenant only)
/// and the body of <c>POST /api/users</c>.
/// </summary>
public class UserSummaryResponse
{
    public Guid UserId { get; set; }

    public string Email { get; set; } = string.Empty;

    public bool IsOwner { get; set; }

    public static UserSummaryResponse From(User user)
        => new() { UserId = user.Id, Email = user.Email, IsOwner = user.IsOwner };
}
