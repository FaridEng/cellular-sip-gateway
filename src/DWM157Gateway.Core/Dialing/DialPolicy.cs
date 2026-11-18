using DWM157Gateway.Core.Configuration;

namespace DWM157Gateway.Core.Dialing;

public sealed class DialPolicy
{
    private readonly DialPolicyConfig _config;

    public DialPolicy(DialPolicyConfig config)
    {
        _config = config;
    }

    public DialDecision Validate(string requestedNumber)
    {
        var normalized = Normalize(requestedNumber);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return DialDecision.Blocked("Dial target is empty.");
        }

        if (_config.BlockedNumbers.Any(blocked => Normalize(blocked) == normalized))
        {
            return DialDecision.Blocked("Emergency or blocked number is not allowed.");
        }

        if (_config.AllowUnlistedNumbers)
        {
            return DialDecision.Allowed(normalized, ApplyDialPlan(normalized));
        }

        var allowed = _config.AllowedDialNumbers.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_config.TestNumber))
        {
            allowed.Add(Normalize(_config.TestNumber));
        }

        if (allowed.Count == 0 || !allowed.Contains(normalized))
        {
            return DialDecision.Blocked("Number is not in TestNumber or AllowedDialNumbers.");
        }

        var transformed = ApplyDialPlan(normalized);
        return DialDecision.Allowed(normalized, transformed);
    }

    private string ApplyDialPlan(string number)
    {
        var result = number;
        if (!string.IsNullOrEmpty(_config.DialPlan.RemovePrefix) &&
            result.StartsWith(_config.DialPlan.RemovePrefix, StringComparison.Ordinal))
        {
            result = result[_config.DialPlan.RemovePrefix.Length..];
        }

        if (!string.IsNullOrEmpty(_config.DialPlan.AddPrefix))
        {
            result = _config.DialPlan.AddPrefix + result;
        }

        return result;
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(c => char.IsDigit(c) || c == '+').ToArray());
    }
}

public sealed record DialDecision(bool IsAllowed, string? NormalizedNumber, string? DialString, string? Reason)
{
    public static DialDecision Allowed(string normalizedNumber, string dialString) =>
        new(true, normalizedNumber, dialString, null);

    public static DialDecision Blocked(string reason) =>
        new(false, null, null, reason);
}
