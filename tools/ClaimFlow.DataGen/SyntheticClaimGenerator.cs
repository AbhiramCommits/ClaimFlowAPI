using ClaimFlow.Shared;
using ClaimFlow.Shared.Dtos;

namespace ClaimFlow.DataGen;

internal sealed record GeneratedDataSet(
    IReadOnlyList<ClaimBundle> Bundles,
    IReadOnlyDictionary<string, int> InjectedErrors);

/// <summary>
/// Deterministic synthetic claim generator. Every record is fake; the seed controls all choices.
/// </summary>
internal sealed class SyntheticClaimGenerator
{
    private static readonly string[] Payers =
        PayerDirectory.All.Select(p => p.PayerId).ToArray();

    private static readonly string[] CptCodes =
    {
        "99213", "99214", "99215", "99396", "93000", "80053", "71046",
        "27447", "70551", "97110", "36415", "45378", "20610", "90471", "12002",
    };

    private static readonly string[] Icd10Codes =
    {
        "J45.909", "E11.9", "I10", "M54.5", "Z00.00", "K21.9", "F41.1",
        "N39.0", "S52.501A", "Z23", "R51.9", "J06.9", "G43.909", "M17.11", "L03.115",
    };

    private static readonly string[] PlacesOfService = { "11", "21", "22", "23", "24", "31", "32", "81", "12" };

    private static readonly string[] GivenNames =
    {
        "Alex", "Jordan", "Taylor", "Morgan", "Casey", "Riley", "Avery", "Quinn",
        "Sam", "Jamie", "Drew", "Cameron", "Reese", "Skyler", "Hayden", "Emerson",
    };

    private static readonly string[] FamilyNames =
    {
        "Nguyen", "Patel", "Garcia", "Smith", "Johnson", "Kim", "Lopez", "Brown",
        "Davis", "Martinez", "Wilson", "Anderson", "Thomas", "Moore", "Jackson", "White",
    };

    private static readonly (string City, string State, string Postal)[] Locations =
    {
        ("Austin", "TX", "78701"), ("Denver", "CO", "80202"), ("Seattle", "WA", "98101"),
        ("Chicago", "IL", "60601"), ("Boston", "MA", "02108"), ("Atlanta", "GA", "30303"),
        ("Phoenix", "AZ", "85004"), ("Portland", "OR", "97205"), ("Nashville", "TN", "37203"),
        ("Minneapolis", "MN", "55401"),
    };

    private static readonly string[] ErrorTypes =
    {
        "missing_member_id",
        "unknown_payer",
        "future_service_date",
        "stale_service_date",
        "invalid_npi",
        "invalid_icd10",
        "invalid_cpt",
        "non_positive_charge",
        "line_total_mismatch",
        "duplicate_line",
        "missing_place_of_service",
    };

    private readonly GeneratorOptions _options;
    private readonly Random _random;
    private readonly string[] _npis;

    public SyntheticClaimGenerator(GeneratorOptions options)
    {
        _options = options;
        _random = new Random(options.Seed);
        _npis = Enumerable.Range(0, 24).Select(_ => CreateNpi(_random)).ToArray();
    }

    public GeneratedDataSet Generate()
    {
        var bundles = new List<ClaimBundle>(_options.Count);
        var injected = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < _options.Count; i++)
        {
            var injectError = _random.NextDouble() < _options.ErrorRate;
            var errorType = injectError ? ErrorTypes[_random.Next(ErrorTypes.Length)] : null;

            if (errorType is not null)
            {
                injected[errorType] = injected.GetValueOrDefault(errorType) + 1;
            }

            bundles.Add(CreateBundle(i, errorType));
        }

        return new GeneratedDataSet(bundles, injected);
    }

    private ClaimBundle CreateBundle(int index, string? errorType)
    {
        var memberNumber = _random.Next(1, 401);
        var memberId = $"MBR-{memberNumber:D6}";
        var payerId = Payers[_random.Next(Payers.Length)];
        var serviceDate = _options.AsOf.AddDays(-_random.Next(1, 171));
        var npi = _npis[_random.Next(_npis.Length)];
        var placeOfService = PlacesOfService[_random.Next(PlacesOfService.Length)];

        var lineCount = _random.Next(1, 5);
        var lineCodes = new List<string>(lineCount);
        var lineCharges = new List<int>(lineCount);
        for (var line = 0; line < lineCount; line++)
        {
            var cpt = CptCodes[_random.Next(CptCodes.Length)];
            var charge = _random.Next(4_500, 120_001) + (line * 137);
            lineCodes.Add(cpt);
            lineCharges.Add(charge);
        }

        var primaryDiagnosis = Icd10Codes[_random.Next(Icd10Codes.Length)];

        switch (errorType)
        {
            case "missing_member_id":
                memberId = string.Empty;
                break;
            case "unknown_payer":
                payerId = "PAYER-UNKNOWN";
                break;
            case "future_service_date":
                serviceDate = _options.AsOf.AddDays(_random.Next(10, 120));
                break;
            case "stale_service_date":
                serviceDate = _options.AsOf.AddDays(-800);
                break;
            case "invalid_npi":
                npi = npi[..9] + (char)('0' + ((npi[9] - '0' + 1) % 10));
                break;
            case "invalid_icd10":
                primaryDiagnosis = "ZZZ999";
                break;
            case "invalid_cpt":
                lineCodes[0] = "ABCDE";
                break;
            case "non_positive_charge":
                lineCharges[0] = 0;
                break;
            case "line_total_mismatch":
                break;
            case "duplicate_line":
                lineCodes.Add(lineCodes[0]);
                lineCharges.Add(lineCharges[0]);
                break;
            case "missing_place_of_service":
                placeOfService = string.Empty;
                break;
        }

        var total = lineCharges.Sum();
        if (errorType == "line_total_mismatch" && total > 0)
        {
            total += 137;
        }

        var claimId = $"CLM-{_options.Seed}-{index:D6}";
        var lines = lineCodes.Select((cpt, i) => new ClaimLineItem
        {
            LineNumber = i + 1,
            CptCode = cpt,
            Icd10Code = errorType == "invalid_icd10" && i == 0 ? "ZZZ999" : primaryDiagnosis,
            PlaceOfService = placeOfService,
            ChargeCents = lineCharges[i],
            Units = 1,
        }).ToArray();

        var location = Locations[_random.Next(Locations.Length)];
        var ssn = $"{_random.Next(100, 1000):D3}-{_random.Next(10, 100):D2}-{_random.Next(1000, 10000):D4}";

        return new ClaimBundle
        {
            ResourceType = "Bundle",
            Id = $"BND-{_options.Seed}-{index:D6}",
            PayerId = payerId,
            MemberId = memberId,
            Patient = new PatientResource
            {
                MemberId = memberId,
                GivenName = GivenNames[_random.Next(GivenNames.Length)],
                FamilyName = FamilyNames[_random.Next(FamilyNames.Length)],
                DateOfBirth = _options.AsOf.AddYears(-_random.Next(18, 85)).AddDays(-_random.Next(0, 365)),
                Ssn = ssn,
                Address = new AddressResource
                {
                    Line1 = $"{_random.Next(100, 9999)} {FamilyNames[_random.Next(FamilyNames.Length)]} Street",
                    City = location.City,
                    State = location.State,
                    PostalCode = location.Postal,
                },
            },
            Coverage = new CoverageResource
            {
                CoverageId = $"COV-{_options.Seed}-{index:D6}",
                PayerId = payerId,
                Status = CoverageStatus.Active,
                EffectiveDate = _options.AsOf.AddYears(-2),
                TerminationDate = null,
                DeductibleRemainingCents = _random.Next(0, 12) == 0 ? _random.Next(5_000, 50_001) : 0,
            },
            Claim = new ClaimResource
            {
                ClaimId = claimId,
                PayerId = payerId,
                MemberId = memberId,
                ProviderNpi = npi,
                PlaceOfService = placeOfService,
                ServiceDate = serviceDate,
                TotalChargeCents = total,
                DiagnosisCodes = new[] { primaryDiagnosis },
                Lines = lines,
            },
        };
    }

    private static string CreateNpi(Random random)
    {
        var payload = "1" + string.Concat(Enumerable.Range(0, 8).Select(_ => random.Next(0, 10)));
        var sum = 0;
        var doubleDigit = true;
        var luhnPayload = "80840" + payload;
        for (var i = luhnPayload.Length - 1; i >= 0; i--)
        {
            var digit = luhnPayload[i] - '0';
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
        return payload + (char)('0' + checkDigit);
    }
}
