using DWM157Gateway.Core.Configuration;
using DWM157Gateway.Core.Dialing;

namespace DWM157Gateway.Core.Tests;

public sealed class DialPolicyTests
{
    [Fact]
    public void BlocksEmergencyNumbers()
    {
        var policy = new DialPolicy(new DialPolicyConfig
        {
            AllowUnlistedNumbers = true,
            AllowedDialNumbers = ["911"]
        });
        var decision = policy.Validate("911");
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void AllowsUnlistedNumberWhenExplicitlyEnabled()
    {
        var policy = new DialPolicy(new DialPolicyConfig { AllowUnlistedNumbers = true });
        var decision = policy.Validate("05553456789");
        Assert.True(decision.IsAllowed);
        Assert.Equal("05553456789", decision.DialString);
    }

    [Fact]
    public void BlocksUnlistedNumbers()
    {
        var policy = new DialPolicy(new DialPolicyConfig { AllowedDialNumbers = ["12345"] });
        var decision = policy.Validate("54321");
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void AllowsConfiguredTestNumber()
    {
        var policy = new DialPolicy(new DialPolicyConfig { TestNumber = "12345" });
        var decision = policy.Validate("12345");
        Assert.True(decision.IsAllowed);
        Assert.Equal("12345", decision.DialString);
    }

    [Fact]
    public void AppliesDialPlanAfterAllowlistValidation()
    {
        var policy = new DialPolicy(new DialPolicyConfig
        {
            AllowedDialNumbers = ["0012345"],
            DialPlan = new DialPlanConfig { RemovePrefix = "00", AddPrefix = "+" }
        });

        var decision = policy.Validate("0012345");
        Assert.True(decision.IsAllowed);
        Assert.Equal("+12345", decision.DialString);
    }

    [Fact]
    public void AppliesDialPlanToUnlistedNumberWhenExplicitlyEnabled()
    {
        var policy = new DialPolicy(new DialPolicyConfig
        {
            AllowUnlistedNumbers = true,
            DialPlan = new DialPlanConfig { RemovePrefix = "00", AddPrefix = "+" }
        });

        var decision = policy.Validate("0012345");
        Assert.True(decision.IsAllowed);
        Assert.Equal("+12345", decision.DialString);
    }
}
