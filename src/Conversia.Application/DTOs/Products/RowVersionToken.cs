using System.Buffers.Binary;

namespace Conversia.Application.DTOs.Products;

/// <summary>
/// The wire form of a product's concurrency token: base64 of the 32-bit row version (PostgreSQL <c>xmin</c>) in big-endian
/// order. Clients treat it as an opaque string and send it back unchanged.
/// </summary>
public static class RowVersionToken
{
    private const int ByteLength = sizeof(uint);

    public static string Encode(uint rowVersion)
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, rowVersion);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// The row version a decoded token stands for. A token of any other length cannot match a stored row, so it maps to 0,
    /// which no stored row carries; it is then reported as a stale write like any other mismatch.
    /// </summary>
    public static uint ToRowVersion(ReadOnlySpan<byte> bytes)
        => bytes.Length == ByteLength ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : 0;
}
