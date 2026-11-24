using System.Diagnostics.CodeAnalysis;

namespace Bequadro.Internal;

internal static class ThrowHelper
{
    [DoesNotReturn]
    public static void InvalidData(string message) => throw new InvalidDataException(message);

    [DoesNotReturn]
    public static T InvalidData<T>(string message) => throw new InvalidDataException(message);

    [DoesNotReturn]
    public static void OutOfBounds(int position, int length) =>
        throw new InvalidDataException(
            $"The buffer is malformed: access at {position} of {length} bytes is out of bounds."
        );

    public static void EnsureLittleEndian()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Bequadro only supports little-endian platforms.");
        }
    }
}
