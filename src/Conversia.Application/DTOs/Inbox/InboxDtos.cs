using System.ComponentModel.DataAnnotations;

namespace Conversia.Application.DTOs.Inbox;

/// <summary>
/// Query string of <c>GET /api/inbox</c> (docs/MESSAGING.md, "Inbox"). Without <see cref="Since"/> it answers the open conversations of the
/// filters, by priority; with it, only the conversations of the filters (open or not) that changed after that instant.
/// </summary>
public class InboxQuery : IValidatableObject
{
    /// <summary>Only the conversations assigned to the advisor linked to the caller.</summary>
    [Display(Name = "Mías")]
    public bool? Mine { get; set; }

    [Display(Name = "Sucursal")]
    public Guid? BranchId { get; set; }

    [Display(Name = "Sin asesor")]
    public bool? Unassigned { get; set; }

    /// <summary>Only the conversations whose last message is the customer's.</summary>
    [Display(Name = "Esperando respuesta")]
    public bool? AwaitingReply { get; set; }

    /// <summary>The <see cref="InboxResponse.Cursor"/> of a previous answer: only what changed since then.</summary>
    [Display(Name = "Desde")]
    public DateTime? Since { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Mine == true && Unassigned == true)
        {
            yield return new ValidationResult("No se puede filtrar por tus conversaciones y por conversaciones sin asesor a la vez.", [nameof(Mine), nameof(Unassigned)]);
        }
    }
}

/// <summary>One conversation of the inbox. Only what the list shows: no message text.</summary>
public class InboxItem
{
    public Guid Id { get; set; }

    public string? ContactName { get; set; }

    /// <summary>The customer's WhatsApp number (the tenant's own data, as in the conversation list).</summary>
    public string ContactWaId { get; set; } = string.Empty;

    public Guid BranchId { get; set; }

    public string? BranchName { get; set; }

    public Guid? AdvisorId { get; set; }

    public string? AdvisorName { get; set; }

    /// <summary><c>Open</c> or <c>Closed</c>. A delta (<see cref="InboxQuery.Since"/>) may carry a closed one: it leaves the inbox.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime LastMessageAt { get; set; }

    public DateTime? LastInboundAt { get; set; }

    /// <summary>The customer wrote last; <see cref="LastInboundAt"/> is then how long they have been waiting.</summary>
    public bool AwaitingReply { get; set; }

    public bool WindowOpen { get; set; }

    public bool WindowClosingSoon { get; set; }

    public DateTime? WindowClosesAt { get; set; }

    public bool IsSandbox { get; set; }

    /// <summary>The AI sales assistant's state (docs/ASSISTANT.md): <c>Waiting</c>, <c>Active</c>, <c>HandedOff</c>, <c>HumanTookOver</c>; null when it is not involved.</summary>
    public string? AssistantState { get; set; }

    /// <summary>The assistant is preparing its answer right now.</summary>
    public bool AssistantGenerating { get; set; }

    /// <summary>When the row last changed (or was created): what the delta compares with.</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>
    /// The row matches the filters of the request. Always true in a full list; in a delta (<c>since</c>) false means the conversation left
    /// the filters (reassigned, answered, another branch) and must be removed from the list, like a closed one.
    /// </summary>
    public bool InFilter { get; set; } = true;
}

public class InboxResponse
{
    public const int MaxItems = 200;

    public IReadOnlyList<InboxItem> Items { get; set; } = [];

    /// <summary>More open conversations match than <see cref="MaxItems"/>: the rest are of lower priority.</summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// A delta (<c>since</c>) had more changes than <see cref="MaxItems"/>: it is not the whole change, and the client must load the full list
    /// again (without <c>since</c>). Always false in a full list.
    /// </summary>
    public bool FullReloadRequired { get; set; }

    /// <summary>
    /// Pass it as <c>since</c> to get only what changed afterwards. It is the database's time, a few seconds back on purpose: an item may come
    /// twice. A delta never reports a deleted conversation (a suppressed contact, deleted sandbox data): clients that use deltas must also
    /// load the full list again from time to time (the Web polls the full list with its <c>ETag</c>).
    /// </summary>
    public DateTime Cursor { get; set; }

    /// <summary>The caller asked for their own conversations but no advisor is linked to their user: the list is empty for that reason.</summary>
    public bool NoAdvisorLinked { get; set; }

    /// <summary>The company is in sandbox mode: the inbox shows its sandbox conversations only.</summary>
    public bool SandboxMode { get; set; }
}

/// <summary>Answer of <c>GET /api/conversations/{id}/version</c>: enough to know whether the conversation changed, for polling.</summary>
public class ConversationVersionResponse
{
    public int MessageCount { get; set; }

    public DateTime LastMessageAt { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>The customer's last message: the start of the 24-hour window.</summary>
    public DateTime? LastInboundAt { get; set; }

    /// <summary>Whether free text can be sent now, as of the answer: the page reloads its composer when this changes.</summary>
    public bool WindowOpen { get; set; }

    /// <summary>Opaque; changes when anything above does (the window too, which closes without any new message).</summary>
    public string Version { get; set; } = string.Empty;
}
