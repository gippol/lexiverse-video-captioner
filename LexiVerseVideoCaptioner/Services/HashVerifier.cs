using System.IO;
using System.Security.Cryptography;

namespace LexiVerseVideoCaptioner.Services;

/// <summary>
/// ダウンロードしたファイルの SHA-256 を検証するヘルパー
/// </summary>
public static class HashVerifier
{
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static bool Matches(string path, string expectedSha256)
        => string.Equals(ComputeSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ハッシュが一致しなければ例外を投げる（ファイルは呼び出し側で削除する）
    /// </summary>
    public static void VerifyFile(string path, string expectedSha256, string displayName)
    {
        var actual = ComputeSha256(path);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{displayName} の検証に失敗しました（改ざん・破損の可能性があります）。\n" +
                $"期待値: {expectedSha256}\n実際: {actual}");
        }
    }
}
