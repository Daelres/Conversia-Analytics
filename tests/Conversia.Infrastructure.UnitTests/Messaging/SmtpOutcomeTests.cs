using Conversia.Application.Reports;
using Conversia.Infrastructure.Email;

namespace Conversia.Infrastructure.UnitTests.Messaging;

/// <summary>How an SMTP failure is classified (docs/REPORTS.md, "Email"): a timeout before the hand-over is retried; one during it is an unknown outcome.</summary>
public class SmtpOutcomeTests
{
    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(OperationCanceledException))]
    public void A_connection_lost_before_the_message_is_handed_over_is_retried(Type exception)
        => Assert.Equal(EmailSendStatus.TransientFailure, SmtpEmailSender.Classify((Exception)Activator.CreateInstance(exception)!, CancellationToken.None)!.Status);

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(OperationCanceledException))]
    public void A_connection_lost_while_handing_the_message_over_is_an_unknown_outcome_never_retried(Type exception)
    {
        var result = SmtpEmailSender.Classify((Exception)Activator.CreateInstance(exception)!, CancellationToken.None, handedOver: true)!;

        Assert.Equal((EmailSendStatus.UnknownOutcome, "smtp_unknown_outcome"), (result.Status, result.ErrorCode));
    }
}
