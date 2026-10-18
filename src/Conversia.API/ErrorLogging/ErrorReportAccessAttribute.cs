using Conversia.API.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Conversia.API.ErrorLogging;

/// <summary>
/// Guards <c>POST /api/diagnostics/errors</c> before its body is read: off (404) unless a report key is configured, then
/// the <c>X-Error-Report-Key</c> header must match it (401, without saying why), then the body is capped at
/// <see cref="MaxBodyBytes"/> — by <c>Content-Length</c> up front and by counting while it is read.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ErrorReportAccessAttribute : Attribute, IResourceFilter
{
    public const string KeyHeader = "X-Error-Report-Key";
    public const int MaxBodyBytes = 64 * 1024;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var http = context.HttpContext;
        var verifier = http.RequestServices.GetRequiredService<ErrorReportKeyVerifier>();

        if (!verifier.IsEnabled)
        {
            context.Result = new NotFoundResult();
        }
        else if (!verifier.Matches(http.Request.Headers[KeyHeader].ToString()))
        {
            context.Result = new UnauthorizedResult();
        }
        else if (http.Request.ContentLength > MaxBodyBytes)
        {
            throw PayloadTooLarge();
        }
        else
        {
            http.Request.Body = new LimitedReadStream(http.Request.Body, MaxBodyBytes);
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }

    private static BadHttpRequestException PayloadTooLarge()
        => new(ProblemTexts.PayloadTooLargeDetail, StatusCodes.Status413PayloadTooLarge);

    /// <summary>A body without <c>Content-Length</c> (chunked) is only known to be too long once more than the limit has been read.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw PayloadTooLarge() : read;
        }
    }
}
