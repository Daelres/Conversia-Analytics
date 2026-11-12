namespace Conversia.Domain.Enums;

/// <summary>Who set one extracted field (service, interest reason, objection, age range, gender). A <see cref="Manual"/> field is never overwritten by a classifier. Stored by name.</summary>
public enum FieldSource
{
    /// <summary>Set by a classifier; the next classification may change it.</summary>
    Auto = 0,

    /// <summary>Set (or confirmed) by a person.</summary>
    Manual = 1,
}
