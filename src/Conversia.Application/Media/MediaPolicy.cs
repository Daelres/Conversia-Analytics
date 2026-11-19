using System.Text;

namespace Conversia.Application.Media;

/// <summary>The WhatsApp media kinds, each named after its message type.</summary>
public enum MediaKind
{
    Image,
    Audio,
    Video,
    Document,
    Sticker,
}

/// <param name="Allowed">The file may be stored and served.</param>
/// <param name="ContentType">The type it is served with: the type of its content, from the allow-list. Null when not allowed.</param>
/// <param name="Reason">Why it was blocked, a short code for the logs (never the file's content or name).</param>
public sealed record MediaInspection(bool Allowed, string? ContentType, string? Reason)
{
    public static MediaInspection Allow(string contentType) => new(true, contentType, null);

    public static MediaInspection Block(string reason) => new(false, null, reason);
}

/// <param name="Kind">How the file is sent: an image or a document.</param>
public sealed record MediaUploadInspection(bool Allowed, MediaKind Kind, string? ContentType, string? Reason);

/// <summary>
/// What may be stored, served and sent (docs/MESSAGING.md, "Media"). There is no antivirus (out of scope, docs/decisiones.md), so the rules
/// are strict:
/// <list type="bullet">
/// <item>the limits of each kind are Meta's (images 5 MB, audio and video 16 MB, documents 100 MB, stickers 500 KB);</item>
/// <item>the type is read from the first bytes of the content, never trusted from the provider or the browser; the file is kept only when its
/// content is one of the types WhatsApp supports for its kind (an allow-list);</item>
/// <item>executables (PE, ELF, Mach-O), scripts (<c>#!</c>) and markup (HTML, SVG, XML) are blocked whatever they claim to be;</item>
/// <item>documents are always served as attachments, never rendered by the browser.</item>
/// </list>
/// </summary>
public static class MediaPolicy
{
    /// <summary>How many bytes of the beginning of a file <see cref="Inspect"/> needs.</summary>
    public const int HeadLength = 4096;

    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";
    public const string Pdf = "application/pdf";
    public const string PlainText = "text/plain";
    public const string Word = "application/msword";
    public const string Excel = "application/vnd.ms-excel";
    public const string PowerPoint = "application/vnd.ms-powerpoint";
    public const string WordOpenXml = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string ExcelOpenXml = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string PowerPointOpenXml = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    // The types Meta supports for each kind (360dialog, "Upload, retrieve or delete media"). Meta writes "video/3gp"; the registered name is video/3gpp.
    private static readonly IReadOnlyDictionary<MediaKind, IReadOnlySet<string>> AllowList = new Dictionary<MediaKind, IReadOnlySet<string>>
    {
        [MediaKind.Image] = new HashSet<string> { Jpeg, Png },
        [MediaKind.Sticker] = new HashSet<string> { Webp },
        [MediaKind.Audio] = new HashSet<string> { "audio/aac", "audio/amr", "audio/mpeg", "audio/mp4", "audio/ogg" },
        [MediaKind.Video] = new HashSet<string> { "video/mp4", "video/3gpp" },
        [MediaKind.Document] = new HashSet<string> { PlainText, Pdf, Word, Excel, PowerPoint, WordOpenXml, ExcelOpenXml, PowerPointOpenXml },
    };

    private static readonly HashSet<string> OleDocuments = [Word, Excel, PowerPoint];
    private static readonly HashSet<string> OpenXmlDocuments = [WordOpenXml, ExcelOpenXml, PowerPointOpenXml];

    /// <summary>Meta's size limit of each kind.</summary>
    public static long MaxBytes(MediaKind kind) => kind switch
    {
        MediaKind.Image => 5L * 1024 * 1024,
        MediaKind.Audio or MediaKind.Video => 16L * 1024 * 1024,
        MediaKind.Document => 100L * 1024 * 1024,
        MediaKind.Sticker => 500L * 1024,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The kind of a message type (<c>image</c>, <c>audio</c>, ...); null for a message that carries no media file.</summary>
    public static MediaKind? KindOf(string? messageType) => messageType switch
    {
        "image" => MediaKind.Image,
        "audio" => MediaKind.Audio,
        "video" => MediaKind.Video,
        "document" => MediaKind.Document,
        "sticker" => MediaKind.Sticker,
        _ => null,
    };

    /// <summary>The message type of a kind (the inverse of <see cref="KindOf"/>).</summary>
    public static string TypeOf(MediaKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>Documents are served as attachments (downloaded, never rendered inline); images, audio and video play in the page.</summary>
    public static bool ServeAsAttachment(string contentType) => AllowList[MediaKind.Document].Contains(contentType) || !IsInlineType(contentType);

    private static bool IsInlineType(string contentType)
        => AllowList[MediaKind.Image].Contains(contentType) || AllowList[MediaKind.Sticker].Contains(contentType)
           || AllowList[MediaKind.Audio].Contains(contentType) || AllowList[MediaKind.Video].Contains(contentType);

    /// <summary>
    /// Decides whether a received file of <paramref name="kind"/> may be stored, from its first bytes (<see cref="HeadLength"/> are enough) and
    /// the type the provider declared. The content decides the type; the declared type only tells apart the Office formats, which share one
    /// container each.
    /// </summary>
    public static MediaInspection Inspect(MediaKind kind, string? declaredMimeType, ReadOnlySpan<byte> head)
    {
        if (DangerousContent(head) is { } danger)
        {
            return MediaInspection.Block(danger);
        }

        var declared = declaredMimeType?.Split(';')[0].Trim().ToLowerInvariant();
        var sniffed = Sniff(head, kind, declared);
        if (sniffed is null)
        {
            // Plain text has no signature: allowed as a document only when it says so and reads as text.
            return kind == MediaKind.Document && declared == PlainText && LooksLikeText(head)
                ? MediaInspection.Allow(PlainText)
                : MediaInspection.Block("unrecognized_content");
        }

        return AllowList[kind].Contains(sniffed) ? MediaInspection.Allow(sniffed) : MediaInspection.Block("type_not_allowed");
    }

    /// <summary>A file a person attaches in the composer: only images (JPEG, PNG) and documents are sent; the content decides which.</summary>
    public static MediaUploadInspection InspectUpload(string? declaredMimeType, ReadOnlySpan<byte> head)
    {
        var image = Inspect(MediaKind.Image, declaredMimeType, head);
        if (image.Allowed)
        {
            return new MediaUploadInspection(true, MediaKind.Image, image.ContentType, null);
        }

        var document = Inspect(MediaKind.Document, declaredMimeType, head);
        return new MediaUploadInspection(document.Allowed, MediaKind.Document, document.ContentType, document.Allowed ? null : document.Reason);
    }

    // Executables, scripts and markup: never stored, whatever the declared type says (an HTML or SVG file served from our origin could run script).
    private static string? DangerousContent(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("MZ"u8) || Starts(head, [0x7F, (byte)'E', (byte)'L', (byte)'F'])
            || Starts(head, [0xFE, 0xED, 0xFA, 0xCE]) || Starts(head, [0xFE, 0xED, 0xFA, 0xCF])
            || Starts(head, [0xCE, 0xFA, 0xED, 0xFE]) || Starts(head, [0xCF, 0xFA, 0xED, 0xFE])
            || Starts(head, [0xCA, 0xFE, 0xBA, 0xBE]))
        {
            return "executable";
        }

        if (head.StartsWith("#!"u8) && !head.StartsWith("#!AMR"u8))
        {
            return "script";
        }

        // Markup after optional whitespace (and a UTF-8 byte order mark): HTML, SVG, XML.
        var start = Starts(head, [0xEF, 0xBB, 0xBF]) ? head[3..] : head;
        var trimmed = start.TrimStart(" \t\r\n"u8);
        return trimmed.Length > 0 && trimmed[0] == (byte)'<' ? "markup" : null;
    }

    private static string? Sniff(ReadOnlySpan<byte> head, MediaKind kind, string? declared)
    {
        if (Starts(head, [0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        if (Starts(head, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return Webp;
        }

        if (head.StartsWith("GIF8"u8))
        {
            return "image/gif";
        }

        if (head.StartsWith("%PDF-"u8))
        {
            return Pdf;
        }

        // The legacy Office formats share the OLE container; the declared type says which one it is.
        if (Starts(head, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            return declared is not null && OleDocuments.Contains(declared) ? declared : "application/x-ole-storage";
        }

        // The Office Open XML formats are zip files with a [Content_Types].xml part; any other zip is not allowed.
        if (head.StartsWith("PK\u0003\u0004"u8))
        {
            var isOffice = head.IndexOf("[Content_Types].xml"u8) >= 0;
            return isOffice && declared is not null && OpenXmlDocuments.Contains(declared) ? declared : "application/zip";
        }

        if (head.StartsWith("OggS"u8))
        {
            return "audio/ogg";
        }

        if (head.StartsWith("#!AMR"u8))
        {
            return "audio/amr";
        }

        if (head.StartsWith("ID3"u8) || (head.Length >= 2 && head[0] == 0xFF && head[1] is 0xFB or 0xF3 or 0xF2))
        {
            return "audio/mpeg";
        }

        if (head.Length >= 2 && head[0] == 0xFF && head[1] is 0xF1 or 0xF9)
        {
            return "audio/aac";
        }

        // ISO base media (MP4, M4A, 3GP): "ftyp" at offset 4, the brand after it.
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(head[8..12]);
            if (brand.StartsWith("3g", StringComparison.Ordinal))
            {
                return kind == MediaKind.Audio ? "audio/mp4" : "video/3gpp";
            }

            return kind == MediaKind.Audio || brand.StartsWith("M4A", StringComparison.Ordinal) ? "audio/mp4" : "video/mp4";
        }

        return null;
    }

    private static bool Starts(ReadOnlySpan<byte> head, ReadOnlySpan<byte> magic) => head.StartsWith(magic);

    // The VBA project of an Office document: a part name in an Open XML zip (names are stored uncompressed in the local headers and the
    // central directory), and the stream name of a legacy (OLE) file, in UTF-16.
    private static readonly byte[][] MacroMarkers =
    [
        "vbaProject.bin"u8.ToArray(),
        Encoding.Unicode.GetBytes("_VBA_PROJECT"),
    ];

    /// <summary>
    /// True when an Office document carries macros (a VBA project), wherever it is in the file: such a document is blocked whatever it is
    /// declared as. The whole stream is scanned in chunks and rewound.
    /// </summary>
    public static bool ContainsMacros(Stream content)
    {
        var start = content.Position;
        var overlap = MacroMarkers.Max(m => m.Length) - 1;
        var buffer = new byte[81920 + overlap];
        var carried = 0;
        try
        {
            int read;
            while ((read = content.Read(buffer, carried, buffer.Length - carried)) > 0)
            {
                var window = buffer.AsSpan(0, carried + read);
                foreach (var marker in MacroMarkers)
                {
                    if (window.IndexOf(marker) >= 0)
                    {
                        return true;
                    }
                }

                carried = Math.Min(overlap, window.Length);
                window[^carried..].CopyTo(buffer);
                
                
            }

            return false;
        }
        finally
        {
            content.Position = start;
        }
    }

    /// <summary>The Office types whose content is scanned for macros.</summary>
    public static bool IsOfficeDocument(string? contentType) => contentType is not null && (OleDocuments.Contains(contentType) || OpenXmlDocuments.Contains(contentType));

    /// <summary>Reads the head of a seekable stream (for <see cref="Inspect"/>) and rewinds it.</summary>
    public static byte[] ReadHead(Stream content)
    {
        var start = content.Position;
        var head = new byte[HeadLength];
        var total = 0;
        int read;
        while (total < head.Length && (read = content.Read(head, total, head.Length - total)) > 0)
        {
            total += read;
        }

        content.Position = start;
        return head[..total];
    }

    // No NUL byte, and valid UTF-8 (a multi-byte character cut at the end of the head is allowed).
    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty || head.Contains((byte)0))
        {
            return false;
        }

        var decoder = new UTF8Encoding(false, true).GetDecoder();
        try
        {
            var chars = new char[head.Length];
            decoder.GetChars(head, chars, flush: false);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
