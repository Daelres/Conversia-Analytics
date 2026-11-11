namespace Conversia.Application.Exceptions;

/// <summary>Why the simulated customer of the internal sandbox could not write (docs/SANDBOX.md). Each one is a 409 with its own code.</summary>
public enum SandboxProblem
{
    /// <summary>The company is not in sandbox mode: there is no simulated customer.</summary>
    ModeOff,

    /// <summary>The conversation is real: a simulated customer never writes into it.</summary>
    NotSandboxConversation,

    /// <summary>The conversation is closed; start a new simulated conversation instead.</summary>
    ConversationClosed,

    /// <summary>The channel is switched off.</summary>
    ChannelInactive,
}

/// <summary>The simulated customer could not write. The API layer maps every <see cref="Problem"/> to 409 and puts <see cref="Code"/> in the problem body.</summary>
public class SandboxException : Exception
{
    public SandboxException(SandboxProblem problem)
        : base(MessageOf(problem))
    {
        Problem = problem;
    }

    public SandboxProblem Problem { get; }

    /// <summary>Stable machine-readable name of the problem, for example <c>sandbox_mode_off</c>.</summary>
    public string Code => CodeOf(Problem);

    public static string CodeOf(SandboxProblem problem) => problem switch
    {
        SandboxProblem.ModeOff => "sandbox_mode_off",
        SandboxProblem.NotSandboxConversation => "not_sandbox_conversation",
        SandboxProblem.ConversationClosed => "conversation_closed",
        SandboxProblem.ChannelInactive => "channel_inactive",
        _ => throw new ArgumentOutOfRangeException(nameof(problem)),
    };

    public static string MessageOf(SandboxProblem problem) => problem switch
    {
        SandboxProblem.ModeOff => "El modo sandbox no está activo para tu empresa. Solo el administrador de Conversia puede activarlo.",
        SandboxProblem.NotSandboxConversation => "Esta conversación es real: el cliente simulado solo puede escribir en conversaciones de prueba.",
        SandboxProblem.ConversationClosed => "La conversación está cerrada y ya no admite mensajes. Inicia una nueva conversación simulada.",
        SandboxProblem.ChannelInactive => "El número de WhatsApp está desactivado.",
        _ => throw new ArgumentOutOfRangeException(nameof(problem)),
    };
}
