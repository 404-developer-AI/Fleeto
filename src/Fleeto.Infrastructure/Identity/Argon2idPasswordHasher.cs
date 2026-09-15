using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Fleeto.Infrastructure.Identity;

/// <summary>
/// Argon2id password hashing (CLAUDE.md, Secrets), replacing Identity's PBKDF2 default. Parameters follow the
/// OWASP recommendation m=46 MiB, t=1, p=1. Hashes are stored in PHC format:
/// <c>$argon2id$v=19$m=47104,t=1,p=1$&lt;salt&gt;$&lt;hash&gt;</c>. Changing the parameters rehashes on next login.
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher<ApplicationUser>
{
    private const int MemoryKib = 47104;
    private const int Iterations = 1;
    private const int Parallelism = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string HashPassword(ApplicationUser user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, MemoryKib, Iterations, Parallelism, HashSize);
        return $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        if (!TryParse(hashedPassword, out var memory, out var iterations, out var parallelism, out var salt, out var expected))
        {
            return PasswordVerificationResult.Failed;
        }

        var actual = Compute(providedPassword, salt, memory, iterations, parallelism, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerificationResult.Failed;
        }

        return memory == MemoryKib && iterations == Iterations && parallelism == Parallelism && expected.Length == HashSize
            ? PasswordVerificationResult.Success
            : PasswordVerificationResult.SuccessRehashNeeded;
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        return argon2.GetBytes(length);
    }

    private static bool TryParse(string phc, out int memory, out int iterations, out int parallelism, out byte[] salt, out byte[] hash)
    {
        memory = iterations = parallelism = 0;
        salt = hash = [];

        var parts = phc.Split('$');
        // "", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
        {
            return false;
        }

        foreach (var setting in parts[3].Split(','))
        {
            var pair = setting.Split('=');
            if (pair.Length != 2 || !int.TryParse(pair[1], out var value) || value <= 0)
            {
                return false;
            }

            switch (pair[0])
            {
                case "m": memory = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
                default: return false;
            }
        }

        // Refuse absurd parameters from a tampered row rather than burning memory.
        if (memory is < 8192 or > 1048576 || iterations > 16 || parallelism > 16)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[4]);
            hash = Convert.FromBase64String(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= 16 && hash.Length is >= 16 and <= 64;
    }
}
