using System.Security.Cryptography;

namespace DatabaseSync.Infrastructure.Management;

public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static (string Hash, string Salt) HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password,
            saltBytes,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    public static bool Verify(string password, string passwordHash, string passwordSalt)
    {
        if (string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(passwordHash) ||
            string.IsNullOrWhiteSpace(passwordSalt))
        {
            return false;
        }

        var saltBytes = Convert.FromBase64String(passwordSalt);
        var expected = Convert.FromBase64String(passwordHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            saltBytes,
            Iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
