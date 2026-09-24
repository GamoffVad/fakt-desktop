using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Files;

namespace Fakt.Infrastructure.Scanning;

/// <summary>Потоковый SHA-256 (CNG, Windows 7+). Файл читается блоками по 1 МБ, память не растёт с размером файла.</summary>
public sealed class FileHasher : IFileHasher
{
    private const int BufferSize = 1024 * 1024;

    public Task<byte[]> ComputeSha256Async(string path, IProgress<long> bytesProgress, CancellationToken cancellationToken)
    {
        return Task.Run(() => Compute(path, bytesProgress, cancellationToken), cancellationToken);
    }

    private static byte[] Compute(string path, IProgress<long> bytesProgress, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(PathUtil.ToLongPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            using var sha = new SHA256Cng();
            var buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha.TransformBlock(buffer, 0, read, null, 0);
                total += read;
                bytesProgress?.Report(total);
            }

            sha.TransformFinalBlock(buffer, 0, 0);
            return sha.Hash;
        }
        catch (IOException ex) when (IsLock(ex))
        {
            throw new IOException($"Файл заблокирован другим процессом: {path}", ex);
        }
    }

    private static bool IsLock(IOException ex)
    {
        var code = Marshal.GetHRForException(ex) & 0xFFFF;
        return code == 32 || code == 33;
    }

    public static string ToHex(byte[] hash)
    {
        return hash == null ? null : BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static byte[] Sha256OfText(string text)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty));
    }
}
