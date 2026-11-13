namespace ClaimFlow.Shared.Validation;

/// <summary>
/// US NPI validation: 10 digits, first digit 1 or 2, Luhn check digit computed over the
/// 80840 prefix plus the first nine digits.
/// </summary>
public static class NpiValidator
{
    public static bool IsValid(string? npi)
    {
        if (npi is null || npi.Length != 10)
        {
            return false;
        }

        foreach (var c in npi)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        if (npi[0] is not ('1' or '2'))
        {
            return false;
        }

        var payload = "80840" + npi[..9];
        var sum = 0;
        var doubleDigit = true;

        for (var i = payload.Length - 1; i >= 0; i--)
        {
            var digit = payload[i] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        var checkDigit = (10 - (sum % 10)) % 10;
        return checkDigit == npi[9] - '0';
    }
}
