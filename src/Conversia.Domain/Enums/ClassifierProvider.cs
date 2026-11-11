namespace Conversia.Domain.Enums;

/// <summary>Which classifier produced an automatic value. Stored by name; a null producer (rows older than the column) is read as <see cref="Rules"/>.</summary>
public enum ClassifierProvider
{
    /// <summary>The rule-based Spanish classifier.</summary>
    Rules = 0,

    /// <summary>The language model.</summary>
    Llm = 1,
}
