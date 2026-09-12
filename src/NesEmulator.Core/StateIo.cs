namespace NesEmulator.Core;

/// <summary>
/// Small helpers for reading save states back. A partial read is a corrupt
/// state rather than something to recover from, so these insist on the whole
/// buffer and throw otherwise.
/// </summary>
internal static class StateIo
{
    internal static void ReadExactly(this BinaryReader reader, byte[] destination)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = reader.Read(destination, offset, destination.Length - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    $"Save state ended after {offset} of {destination.Length} bytes.");
            }

            offset += read;
        }
    }
}
