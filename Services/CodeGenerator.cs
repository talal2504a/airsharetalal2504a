using System.Security.Cryptography;

namespace AirShare.Services;

/// <summary>
/// Cryptographically secure 6-digit code generator.
/// RandomNumberGenerator use hota hai — isliye guess karna mushkil hai.
/// </summary>
public static class CodeGenerator
{
    /// <summary>Code ki length. Spec ke mutabik 6 digits.</summary>
    private const int Length = 6;

    /// <summary>
    /// "000000" se "999999" tak ka random code deta hai.
    ///
    /// GetInt32() crypto-secure hai aur UNIFORM distribution deta hai.
    /// (Random.Next(10) me modulo bias hota hai — 0 thoda zyada aata hai.
    ///  Chhota farak hai, par code guessing ka kaam hota hai, isliye sahi method use karo.)
    /// </summary>
    public static string Generate()
    {
        Span<char> buffer = stackalloc char[Length];

        for (var i = 0; i < Length; i++)
        {
            // '0' (48) + random 0-9 = digit char
            buffer[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }

        return new string(buffer);
    }
}