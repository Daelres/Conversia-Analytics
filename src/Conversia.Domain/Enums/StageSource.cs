namespace Conversia.Domain.Enums;

/// <summary>Who decided the stage of an opportunity. A <see cref="Manual"/> stage is never changed by the classifier. Stored by name.</summary>
public enum StageSource
{
    /// <summary>Set by the system: the classifier, or the close-by-inactivity rule.</summary>
    Auto = 0,

    /// <summary>Set by a person through the API.</summary>
    Manual = 1,
}
