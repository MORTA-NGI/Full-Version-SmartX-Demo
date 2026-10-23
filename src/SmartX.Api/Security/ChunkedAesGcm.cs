using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SmartX.Api.Security;

/// <summary>
/// Streaming authenticated encryption for uploaded files (AES-256-GCM in 64 KB chunks).
/// <para>
/// <c>AesGcm</c> is a one-shot API, so large files are split into chunks that are encrypted independently as
/// they stream in from the multipart request. Memory use stays at ~128 KB regardless of file size.
/// Each chunk gets a unique nonce (8-byte random file prefix + 4-byte counter) and its index + "final" flag are
/// bound in as associated data, so chunks cannot be reordered, dropped or truncated without detection.
/// </para>
/// File format: <c>"SXE1" | prefix(8) | chunkSize(4) | { len(4) | final(1) | tag(16) | ciphertext(len) }*</c>
/// </summary>
public static class ChunkedAesGcm
{
    private const int ChunkSize = 64 * 1024;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "SXE1"u8.ToArray();

    public sealed class FileTooLargeException(long limit) : Exception($"File exceeds the {limit / 1024 / 1024} MB limit.");

    public static async Task<(long PlainBytes, string Sha256Hex)> EncryptAsync(Stream input, Stream output, byte[] key, long maxBytes, CancellationToken ct)
    {
        var prefix = RandomNumberGenerator.GetBytes(8);
        var header = new byte[16];
        Magic.CopyTo(header, 0);
        prefix.CopyTo(header, 4);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), ChunkSize);
        await output.WriteAsync(header, ct);

        using var aes = new AesGcm(key, TagSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] plain = ArrayPool<byte>.Shared.Rent(ChunkSize);
        byte[] cipher = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var tag = new byte[TagSize];
        var nonce = new byte[12];
        var aad = new byte[9];
        var chunkHeader = new byte[5];
        long total = 0;
        uint index = 0;

        try
        {
            while (true)
            {
                int read = await ReadFullAsync(input, plain.AsMemory(0, ChunkSize), ct);
                total += read;
                if (total > maxBytes) throw new FileTooLargeException(maxBytes);
                bool final = read < ChunkSize;

                hash.AppendData(plain, 0, read);
                prefix.CopyTo(nonce, 0);
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
                BinaryPrimitives.WriteUInt64BigEndian(aad, index);
                aad[8] = final ? (byte)1 : (byte)0;

                aes.Encrypt(nonce, plain.AsSpan(0, read), cipher.AsSpan(0, read), tag, aad);

                BinaryPrimitives.WriteInt32BigEndian(chunkHeader, read);
                chunkHeader[4] = aad[8];
                await output.WriteAsync(chunkHeader, ct);
                await output.WriteAsync(tag, ct);
                await output.WriteAsync(cipher.AsMemory(0, read), ct);
                index++;

                if (final) break;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            ArrayPool<byte>.Shared.Return(plain);
            ArrayPool<byte>.Shared.Return(cipher);
        }

        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static async Task DecryptAsync(Stream input, Stream output, byte[] key, CancellationToken ct)
    {
        var header = new byte[16];
        if (await ReadFullAsync(input, header, ct) != 16 || !header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Not a Smart-X encrypted file.");
        var prefix = header.AsSpan(4, 8).ToArray();
        int chunkSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12));

        using var aes = new AesGcm(key, TagSize);
        byte[] cipher = ArrayPool<byte>.Shared.Rent(chunkSize);
        byte[] plain = ArrayPool<byte>.Shared.Rent(chunkSize);
        var tag = new byte[TagSize];
        var nonce = new byte[12];
        var aad = new byte[9];
        var chunkHeader = new byte[5];
        uint index = 0;

        try
        {
            while (true)
            {
                if (await ReadFullAsync(input, chunkHeader, ct) != 5)
                    throw new CryptographicException("Encrypted file is truncated.");
                int len = BinaryPrimitives.ReadInt32BigEndian(chunkHeader);
                bool final = chunkHeader[4] == 1;
                if (len < 0 || len > chunkSize) throw new CryptographicException("Corrupt chunk length.");
                if (await ReadFullAsync(input, tag, ct) != TagSize || await ReadFullAsync(input, cipher.AsMemory(0, len), ct) != len)
                    throw new CryptographicException("Encrypted file is truncated.");

                prefix.CopyTo(nonce, 0);
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
                BinaryPrimitives.WriteUInt64BigEndian(aad, index);
                aad[8] = chunkHeader[4];

                aes.Decrypt(nonce, cipher.AsSpan(0, len), tag, plain.AsSpan(0, len), aad); // throws if tampered
                await output.WriteAsync(plain.AsMemory(0, len), ct);
                index++;
                if (final) break;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            ArrayPool<byte>.Shared.Return(plain);
            ArrayPool<byte>.Shared.Return(cipher);
        }
    }

    private static async Task<int> ReadFullAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await s.ReadAsync(buffer[total..], ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
