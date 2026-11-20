using Conversia.Application.DTOs.Contacts;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/contacts")]
[Authorize]
public class ContactsController : ControllerBase
{
    private readonly IContactSuppressionService _suppression;

    public ContactsController(IContactSuppressionService suppression)
    {
        _suppression = suppression;
    }

    /// <summary>
    /// Suppresses a contact on request of the customer (Ley 1581; docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression"): its
    /// conversations, messages and opportunities are deleted, irreversibly, and its old messages can never bring it back. A contact of
    /// another company is the same <c>404</c> as a missing one.
    /// </summary>
    [HttpPost("{id:guid}/suppression")]
    [Authorize(Policy = nameof(Permission.ContactsSuppress))]
    public async Task<ActionResult<ContactSuppressionResponse>> Suppress(Guid id, CancellationToken cancellationToken)
        => Ok(await _suppression.SuppressAsync(User.GetTenantId(), User.GetUserId()!.Value, id, cancellationToken));
}
