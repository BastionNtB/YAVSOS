using System;
using System.Buffers;
using System.IO;
using System.Text;
using YAVSOS.Core.Policy;

namespace YAVSOS.Networking;

internal static class CategoryPolicyFileReader
{
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    // The caller owns the stream and applies policy only after a successful read.
    // Accept a stream so byte limits, growth, and decoding failures can be tested
    // without opening real files or loading Unity/BepInEx.
    public static bool TryRead(Stream stream, out string payload, out string diagnostic)
    {
        payload = "";
        diagnostic = "";
        try
        {
            if (stream.Length > CategoryPolicyCodec.MaxPayloadBytes)
                throw new InvalidDataException("Category file exceeds 256 KiB.");
            // Bound allocation even if the file grows after the length check.
            var limit = CategoryPolicyCodec.MaxPayloadBytes + 1;
            var bytes = ArrayPool<byte>.Shared.Rent(limit);
            try
            {
                var count = 0;
                int read;
                // Pool buckets may be larger: the logical read bound stays max+1.
                while (count < limit && (read = stream.Read(bytes, count, limit - count)) > 0)
                    count += read;
                if (count > CategoryPolicyCodec.MaxPayloadBytes)
                    throw new InvalidDataException("Category file exceeds 256 KiB.");
                payload = StrictUtf8.GetString(bytes, 0, count).TrimStart('\uFEFF');
            }
            finally { ArrayPool<byte>.Shared.Return(bytes); }
            return true;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
            || error is DecoderFallbackException || error is InvalidDataException)
        {
            diagnostic = error.Message;
            return false;
        }
    }
}
