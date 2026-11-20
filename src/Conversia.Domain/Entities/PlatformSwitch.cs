using Conversia.Domain.Common;

namespace Conversia.Domain.Entities;

/// <summary>
/// A platform-wide switch the administrator flips at run time (docs/PLATFORM_ADMIN.md, "Assistant"). Not tenant-scoped. A missing row is the
/// switch's default (<see cref="DefaultOf"/>). Each change is audited in <c>PlatformAuditLogs</c>.
/// </summary>
public class PlatformSwitch
{
    /// <summary>The AI sales assistant for every company (docs/ASSISTANT.md, "Kill switches"). On by default: each company is off until it opts in.</summary>
    public const string Assistant = "assistant";

    public string Name { get; private set; } = string.Empty;

    public bool Enabled { get; private set; }

    public DateTime ChangedAt { get; private set; }

    public Guid? ChangedBy { get; private set; }

    public static bool DefaultOf(string name) => name == Assistant;

    public static PlatformSwitch Create(string name, bool enabled, Guid? changedBy, DateTime now)
        => new() { Name = name, Enabled = enabled, ChangedBy = changedBy, ChangedAt = UtcTime.Normalize(now) };

    /// <summary>Returns false, and changes nothing, when it already was that way.</summary>
    public bool Set(bool enabled, Guid? changedBy, DateTime now)
    {
        if (Enabled == enabled)
        {
            return false;
        }

        Enabled = enabled;
        ChangedBy = changedBy;
        ChangedAt = UtcTime.Normalize(now);
        return true;
    }
}
