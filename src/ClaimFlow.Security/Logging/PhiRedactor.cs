using System.Security.Cryptography;
using System.Text;

namespace ClaimFlow.Security.Logging;

/// <summary>
/// Central PHI policy. Every field in <see cref="SensitiveFieldNames"/> is masked before it can
/// reach a log sink: identifiers keep only their last four characters, names keep an initial,
/// dates are hashed, addresses are dropped, and raw request bodies are SHA-256 hashed.
/// </summary>
public static class PhiRedactor
{
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MemberId",
        "memberId",
        "Ssn",
        "SSN",
        "SocialSecurityNumber",
        "DateOfBirth",
        "DOB",
        "BirthDate",
        "GivenName",
        "FirstName",
        "FamilyName",
        "LastName",
        "PatientName",
        "Address",
        "AddressLine1",
        "Line1",
        "PostalCode",
        "RawBody",
        "RequestBody",
        "Body",
        "Payload",
    };

    public static IReadOnlyCollection<string> SensitiveFieldNames { get; } = SensitiveNames.ToArray();

    public static bool IsSensitiveName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SensitiveNames.Contains(Normalize(name));

    public static string Redact(string fieldName, object? value)
    {
        if (value is null)
        {
            return "***";
        }

        var normalized = Normalize(fieldName);
        var text = value.ToString() ?? string.Empty;
        return normalized switch
        {
            "MemberId" => MaskLast4(text),
            "Ssn" or "SocialSecurityNumber" => MaskSsn(text),
            "DateOfBirth" or "DOB" or "BirthDate" => "DOB#" + ShortHash(text),
            "GivenName" or "FirstName" or "FamilyName" or "LastName" or "PatientName" => MaskName(text),
            "Address" or "AddressLine1" or "Line1" or "PostalCode" => "***REDACTED-ADDRESS***",
            "RawBody" or "RequestBody" or "Body" or "Payload" => HashBody(text),
            _ => "***",
        };
    }

    public static string MaskLast4(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 4 ? "****" : "****" + trimmed[^4..];
    }

    public static string MaskSsn(string value)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 4 ? "***-**-" + digits[^4..] : "***-**-****";
    }

    public static string MaskName(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? "***" : trimmed[..1] + "***";
    }

    public static string HashBody(string body) => "sha256:" + ShortHash(body, 32);

    public static string ShortHash(string value, int length = 12)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "empty";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex[..Math.Min(length, hex.Length)];
    }

    private static string Normalize(string name)
    {
        var cleaned = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return cleaned.ToLowerInvariant() switch
        {
            "memberid" => "MemberId",
            "ssn" => "Ssn",
            "socialsecuritynumber" => "SocialSecurityNumber",
            "dateofbirth" or "dob" or "birthdate" => "DateOfBirth",
            "givenname" or "firstname" => "GivenName",
            "familyname" or "lastname" or "patientname" => "FamilyName",
            "address" or "addressline1" or "line1" or "postalcode" => "Address",
            "rawbody" or "requestbody" or "body" or "payload" => "RawBody",
            _ => cleaned,
        };
    }
}
