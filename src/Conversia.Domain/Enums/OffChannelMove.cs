namespace Conversia.Domain.Enums;

/// <summary>Whether, and where, the conversation moved out of the WhatsApp chat. Stored by name.</summary>
public enum OffChannelMove
{
    /// <summary>No sign that it moved (or nothing could be read).</summary>
    None = 0,

    /// <summary>A phone call ("te llamo y cerramos").</summary>
    PhoneCall = 1,

    /// <summary>In person, at a branch ("pasa por la sede").</summary>
    InPerson = 2,

    /// <summary>Another channel: another number, Instagram, email ("escríbeme al otro número").</summary>
    OtherChannel = 3,
}
