using System.Text;
using Bequadro.Schema.Common;
using Bequadro.Schema.Syntax;

namespace Bequadro.Schema;

/// <summary>
/// The fingerprint of a schema set that every target's generated code holds (SER-015): a hash of each file's tokens,
/// so white space, line endings, comments and paths don't change it.
/// </summary>
internal static class SchemaFingerprint
{
    /// <summary>
    /// The digest of one file's tokens, as lowercase hex. Each token is its kind, the length of its text and the text's
    /// UTF-16 code units, so no two token lists give the same bytes. A string literal's text is its value, so escapes
    /// that spell the same characters give the same digest.
    /// </summary>
    public static string OfFile(IEnumerable<Token> tokens)
    {
        using var bytes = new MemoryStream();
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.EndOfFile)
            {
                continue;
            }

            bytes.WriteByte((byte)token.Kind);
            WriteInt32(bytes, token.Text.Length);
            foreach (var c in token.Text)
            {
                bytes.WriteByte((byte)c);
                bytes.WriteByte((byte)(c >> 8));
            }
        }

        return ToHex(Sha256.Hash(bytes.ToArray()), 32);
    }

    /// <summary>
    /// The fingerprint of a schema set from its files' digests: the first 128 bits of the hash of the digests in
    /// ordinal order, as 32 lowercase hex digits. The order of the files and their paths don't matter.
    /// </summary>
    public static string Combine(IEnumerable<string> fileDigests)
    {
        var sorted = fileDigests.OrderBy(d => d, StringComparer.Ordinal);
        return ToHex(Sha256.Hash(Encoding.ASCII.GetBytes(string.Concat(sorted))), 16);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        for (var i = 0; i < 4; i++)
        {
            stream.WriteByte((byte)(value >> (8 * i)));
        }
    }

    private static string ToHex(byte[] bytes, int count)
    {
        var hex = new StringBuilder(count * 2);
        for (var i = 0; i < count; i++)
        {
            hex.Append(bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }
}
