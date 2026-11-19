using System.Text;
using Conversia.Application.Media;

namespace Conversia.Application.UnitTests.Media;

/// <summary>
/// What may be stored and served (docs/MESSAGING.md, "Media"): the limits per kind (Meta's), the content checked against the declared type, and a
/// strict allow-list with executables and scripts blocked. The files are synthetic: a few magic bytes.
/// </summary>
public class MediaPolicyTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Gif = Encoding.ASCII.GetBytes("GIF89a......");
    private static readonly byte[] Webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%âã");
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0];
    private static readonly byte[] OpenXml = [.. "PK\u0003\u0004"u8.ToArray(), .. new byte[26], .. "[Content_Types].xml"u8.ToArray()];
    private static readonly byte[] PlainZip = [.. "PK\u0003\u0004"u8.ToArray(), .. new byte[26], .. "payload.exe"u8.ToArray()];
    private static readonly byte[] Ogg = Encoding.ASCII.GetBytes("OggS\0\u0002\0\0\0\0");
    private static readonly byte[] Mp3 = Encoding.ASCII.GetBytes("ID3\u0004\0\0\0\0\0\0");
    private static readonly byte[] Adts = [0xFF, 0xF1, 0x50, 0x80, 0, 0];
    private static readonly byte[] Amr = Encoding.ASCII.GetBytes("#!AMR\n....");
    private static readonly byte[] Mp4 = [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];
    private static readonly byte[] ThreeGp = [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'3', (byte)'g', (byte)'p', (byte)'4'];
    private static readonly byte[] M4a = [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'M', (byte)'4', (byte)'A', (byte)' '];
    private static readonly byte[] Exe = [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0];
    private static readonly byte[] Elf = [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0];
    private static readonly byte[] MachO = [0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1];
    private static readonly byte[] Shell = Encoding.ASCII.GetBytes("#!/bin/sh\nrm -rf /\n");
    private static readonly byte[] Html = Encoding.ASCII.GetBytes("  <!DOCTYPE html><script>alert(1)</script>");
    private static readonly byte[] Svg = Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"x\">");
    private static readonly byte[] Text = Encoding.UTF8.GetBytes("Cotización: depilación láser, 6 sesiones.\n");

    public static TheoryData<MediaKind, string?, byte[], string> Allowed => new()
    {
        { MediaKind.Image, "image/jpeg", Jpeg, "image/jpeg" },
        { MediaKind.Image, "image/png", Png, "image/png" },
        { MediaKind.Image, "image/jpeg", Png, "image/png" }, // the content decides, within the kind
        { MediaKind.Sticker, "image/webp", Webp, "image/webp" },
        { MediaKind.Document, "application/pdf", Pdf, "application/pdf" },
        { MediaKind.Document, "application/msword", Ole, "application/msword" },
        { MediaKind.Document, "application/vnd.ms-excel", Ole, "application/vnd.ms-excel" },
        { MediaKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", OpenXml, "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { MediaKind.Document, "text/plain", Text, "text/plain" },
        { MediaKind.Audio, "audio/ogg", Ogg, "audio/ogg" },
        { MediaKind.Audio, "audio/mpeg", Mp3, "audio/mpeg" },
        { MediaKind.Audio, "audio/aac", Adts, "audio/aac" },
        { MediaKind.Audio, "audio/amr", Amr, "audio/amr" },
        { MediaKind.Audio, "audio/mp4", M4a, "audio/mp4" },
        { MediaKind.Video, "video/mp4", Mp4, "video/mp4" },
        { MediaKind.Video, "video/3gpp", ThreeGp, "video/3gpp" },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void AnAllowedFile_IsServedWithTheTypeOfItsContent(MediaKind kind, string? declared, byte[] head, string expected)
    {
        var inspection = MediaPolicy.Inspect(kind, declared, head);

        Assert.True(inspection.Allowed, inspection.Reason);
        Assert.Equal(expected, inspection.ContentType);
    }

    public static TheoryData<MediaKind, string?, byte[]> Blocked => new()
    {
        { MediaKind.Document, "application/pdf", Exe },
        { MediaKind.Document, "application/octet-stream", Elf },
        { MediaKind.Document, "application/pdf", MachO },
        { MediaKind.Document, "text/plain", Shell },
        { MediaKind.Document, "text/plain", Html },
        { MediaKind.Image, "image/svg+xml", Svg },
        { MediaKind.Image, "image/gif", Gif }, // not on Meta's list
        { MediaKind.Image, "image/jpeg", Pdf }, // the content is not an image
        { MediaKind.Audio, "audio/ogg", Jpeg },
        { MediaKind.Document, "application/zip", PlainZip }, // a zip that is not an Office document
        { MediaKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", PlainZip }, // a zip claiming to be one
        { MediaKind.Document, "application/pdf", OpenXml }, // an Office container declared as something else
        { MediaKind.Document, "application/x-msdownload", Text },
        { MediaKind.Document, null, [0, 1, 2, 3, 4, 5] }, // unrecognized binary
        { MediaKind.Video, "video/mp4", Ogg },
        { MediaKind.Sticker, "image/webp", Png },
    };

    [Theory]
    [MemberData(nameof(Blocked))]
    public void ADangerousOrMismatchedFile_IsBlocked(MediaKind kind, string? declared, byte[] head)
    {
        var inspection = MediaPolicy.Inspect(kind, declared, head);

        Assert.False(inspection.Allowed);
        Assert.Null(inspection.ContentType);
    }

    [Theory]
    [InlineData(MediaKind.Image, 5 * 1024 * 1024)]
    [InlineData(MediaKind.Audio, 16 * 1024 * 1024)]
    [InlineData(MediaKind.Video, 16 * 1024 * 1024)]
    [InlineData(MediaKind.Document, 100 * 1024 * 1024)]
    [InlineData(MediaKind.Sticker, 500 * 1024)]
    public void TheLimitOfEachKind_IsMetas(MediaKind kind, long bytes)
        => Assert.Equal(bytes, MediaPolicy.MaxBytes(kind));

    [Theory]
    [InlineData("image", MediaKind.Image)]
    [InlineData("audio", MediaKind.Audio)]
    [InlineData("video", MediaKind.Video)]
    [InlineData("document", MediaKind.Document)]
    [InlineData("sticker", MediaKind.Sticker)]
    public void TheKindComesFromTheMessageType(string type, MediaKind kind)
        => Assert.Equal(kind, MediaPolicy.KindOf(type));

    [Theory]
    [InlineData("text")]
    [InlineData("media_placeholder")]
    [InlineData("unknown")]
    public void AMessageThatIsNotMedia_HasNoKind(string type)
        => Assert.Null(MediaPolicy.KindOf(type));

    [Fact]
    public void AnUploadedImage_IsSentAsAnImage()
    {
        var upload = MediaPolicy.InspectUpload("image/png", Png);

        Assert.True(upload.Allowed);
        Assert.Equal((MediaKind.Image, "image/png"), (upload.Kind, upload.ContentType));
    }

    [Fact]
    public void AnUploadedPdf_IsSentAsADocument()
    {
        var upload = MediaPolicy.InspectUpload("application/pdf", Pdf);

        Assert.Equal((true, MediaKind.Document, "application/pdf"), (upload.Allowed, upload.Kind, upload.ContentType));
    }

    [Theory]
    [MemberData(nameof(UploadsNotAllowed))]
    public void AnUploadOutsideImagesAndDocuments_OrDangerous_IsRefused(string declared, byte[] head)
        => Assert.False(MediaPolicy.InspectUpload(declared, head).Allowed);

    public static TheoryData<string, byte[]> UploadsNotAllowed => new()
    {
        { "audio/ogg", Ogg },
        { "video/mp4", Mp4 },
        { "application/pdf", Exe },
        { "image/svg+xml", Svg },
        { "text/html", Html },
        { "image/gif", Gif },
    };

    [Fact]
    public void OnlyDocumentsAreServedAsAttachments()
    {
        Assert.True(MediaPolicy.ServeAsAttachment("application/pdf"));
        Assert.True(MediaPolicy.ServeAsAttachment("text/plain"));
        Assert.False(MediaPolicy.ServeAsAttachment("image/jpeg"));
        Assert.False(MediaPolicy.ServeAsAttachment("audio/ogg"));
        Assert.False(MediaPolicy.ServeAsAttachment("video/mp4"));
    }

    private static MemoryStream Padded(byte[] head, string tail, bool utf16 = false)
    {
        var stream = new MemoryStream();
        stream.Write(head);
        stream.Write(new byte[20_000]); // past the head the type check reads
        stream.Write(utf16 ? Encoding.Unicode.GetBytes(tail) : Encoding.ASCII.GetBytes(tail));
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void AnOpenXmlDocumentWithAVbaProject_IsBlocked_WhereverThePartIs()
        => Assert.True(MediaPolicy.ContainsMacros(Padded(OpenXml, "word/vbaProject.bin")));

    [Fact]
    public void ALegacyOfficeDocumentWithAVbaProject_IsBlocked()
        => Assert.True(MediaPolicy.ContainsMacros(Padded(Ole, "_VBA_PROJECT_CUR", utf16: true)));

    [Fact]
    public void ADocumentWithoutMacros_IsNotFlagged_AndTheStreamIsRewound()
    {
        var stream = Padded(OpenXml, "word/document.xml");

        Assert.False(MediaPolicy.ContainsMacros(stream));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void APatternSplitAcrossReadBuffers_IsStillFound()
    {
        // Wherever the read buffers end (they hold about 80 KB), a part name cut in two is still found.
        for (var offset = 81_880; offset < 81_980; offset++)
        {
            var stream = new MemoryStream();
            stream.Write(OpenXml);
            stream.Write(new byte[offset - OpenXml.Length]);
            stream.Write(Encoding.ASCII.GetBytes("xl/vbaProject.bin"));
            stream.Position = 0;

            Assert.True(MediaPolicy.ContainsMacros(stream), $"not found at {offset}");
        }
    }
}
