using ComplianceMonitor.Api.Classification;
using ComplianceMonitor.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Tests.Classification;

public sealed class HuggingFaceOptionsTests
{
    private const string SecretToken = "hf_must_not_leak";

    private static ValidateOptionsResult Validate(HuggingFaceOptions options) =>
        new HuggingFaceOptionsValidator().Validate(null, options);

    [Fact]
    public void Validate_DefaultsWithToken_Succeeds() =>
        Assert.True(Validate(new HuggingFaceOptions { ApiToken = SecretToken }).Succeeded);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingToken_FailsNamingConfigKey(string token)
    {
        var result = Validate(new HuggingFaceOptions { ApiToken = token });

        Assert.True(result.Failed);
        Assert.Contains("HuggingFace:ApiToken", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("HuggingFace__ApiToken", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_InvalidValues_ReportsEachKeyWithoutEchoingValues()
    {
        var result = Validate(new HuggingFaceOptions
        {
            ApiToken = SecretToken,
            BaseUrl = new Uri("http://insecure.example/"),
            Model = " ",
            TimeoutSeconds = 0,
            ConfidenceFloor = 1.5,
        });

        Assert.True(result.Failed);
        Assert.Equal(5, result.Failures.Count());
        Assert.Contains("HuggingFace:BaseUrl", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("HuggingFace:Model", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("HuggingFace:TimeoutSeconds", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("HuggingFace:AttemptTimeoutSeconds", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("HuggingFace:ConfidenceFloor", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretToken, result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("insecure.example", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, 1, true)]
    [InlineData(30, 30, true)]
    [InlineData(30, 31, false)]
    [InlineData(30, 0, false)]
    public void Validate_AttemptTimeoutMustFitInTotal(int total, int attempt, bool valid) =>
        Assert.Equal(valid, Validate(new HuggingFaceOptions { ApiToken = SecretToken, TimeoutSeconds = total, AttemptTimeoutSeconds = attempt }).Succeeded);

    [Theory]
    [InlineData(-0.01, false)]
    [InlineData(0.0, true)]
    [InlineData(1.0, true)]
    [InlineData(1.01, false)]
    [InlineData(double.NaN, false)]
    public void Validate_ConfidenceFloorRange(double floor, bool valid) =>
        Assert.Equal(valid, Validate(new HuggingFaceOptions { ApiToken = SecretToken, ConfidenceFloor = floor }).Succeeded);

    [Theory]
    [InlineData("https://router.huggingface.co/hf-inference/models/")]
    [InlineData("https://router.huggingface.co/hf-inference/models")]
    public void ModelUrl_JoinsBaseUrlAndModel(string baseUrl) =>
        Assert.Equal(
            new Uri("https://router.huggingface.co/hf-inference/models/facebook/bart-large-mnli"),
            new HuggingFaceOptions { BaseUrl = new Uri(baseUrl) }.ModelUrl);

    [Fact]
    public void Startup_WithoutToken_FailsNamingConfigKey()
    {
        using var factory = new TokenlessApiFactory();

        // The host aggregates every failed ValidateOnStart (HF options, and the resilience options that read them).
        var ex = Assert.Throws<AggregateException>(() => factory.CreateClient());

        Assert.All(ex.InnerExceptions, inner =>
        {
            Assert.IsType<OptionsValidationException>(inner);
            Assert.Contains("HuggingFace:ApiToken", inner.Message, StringComparison.Ordinal);
        });
    }

    private sealed class TokenlessApiFactory : ApiFactory
    {
        protected override string? ApiToken => "";
    }
}
