using ComplianceMonitor.Application.Abstractions;
using ComplianceMonitor.Application.Compliance.Models;
using ComplianceMonitor.Application.Errors;
using ComplianceMonitor.Infrastructure.Integrations.HuggingFace.Strategies;
using Microsoft.Extensions.Options;

namespace ComplianceMonitor.Infrastructure.Integrations.HuggingFace;

/// <summary>
/// <see cref="IComplianceModelGateway"/> on Hugging Face zero-shot NLI. Everything provider-specific stops here:
/// HF failures become <see cref="ModelGatewayException"/>, HF scores become a <see cref="ModelEvaluation"/>.
/// </summary>
public sealed class HuggingFaceComplianceModelGateway(
    HuggingFaceZeroShotClient client, ILabelStrategy strategy, IOptions<HuggingFaceOptions> options) : IComplianceModelGateway
{
    private readonly HuggingFaceZeroShotClient _client = client;
    private readonly ILabelStrategy _strategy = strategy;
    private readonly string _modelId = options.Value.Model;

    public async Task<ModelEvaluation> EvaluateAsync(string action, string guideline, CancellationToken cancellationToken)
    {
        var prompt = _strategy.Build(action, guideline);

        // HF silently truncates over-long input, which can flip the answer: never send it.
        var budget = ModelInputBudget.Check(prompt);
        if (!budget.Fits)
        {
            throw new ModelGatewayException(
                ModelGatewayFailureKind.InputTooLong,
                $"The action and guideline together are too long for the model: they may need up to {budget.TokenUpperBound} " +
                $"tokens and it reads {budget.MaxTokens}. Shorten either one.");
        }

        try
        {
            var scores = await _client.ClassifyAsync(prompt.ToRequest(), cancellationToken);
            return ZeroShotScoreMapper.ToEvaluation(scores, prompt, _modelId, _strategy.Name);
        }
        catch (HuggingFaceTransientException ex)
        {
            throw new ModelGatewayException(
                ex.IsTimeout ? ModelGatewayFailureKind.Timeout : ModelGatewayFailureKind.Unavailable, ex.Message, ex.RetryAfter, ex);
        }
        catch (HuggingFacePermanentException ex)
        {
            var kind = ex switch
            {
                { UpstreamStatusCode: 402 } => ModelGatewayFailureKind.CreditsExhausted,
                { IsMalformedResponse: true } => ModelGatewayFailureKind.InvalidResponse,
                _ => ModelGatewayFailureKind.Rejected,
            };
            throw new ModelGatewayException(kind, ex.Message, null, ex);
        }
    }
}
