namespace Conversia.Domain.Enums;

/// <summary>The main objection a customer raised during a conversation. Stored by name.</summary>
public enum ObjectionType
{
    None = 0,
    Price = 1,
    Timing = 2,
    Location = 3,
    Trust = 4,
    Pain = 5,
    Other = 6,
}
