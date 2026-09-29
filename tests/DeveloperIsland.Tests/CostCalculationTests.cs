using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Pricing;

namespace DeveloperIsland.Tests;

public class CostCalculationTests
{
    private static readonly ModelPricingService UsdPricing = new(new PricingOverrides { UsdToEur = 1m });

    [Fact]
    public void Prices_input_and_output_per_million_tokens()
    {
        // Opus 5.5: $4 input, $20 output per MTok.
        var estimate = UsdPricing.Estimate("claude-opus-5-5", new TokenCounts(1_000_000, 500_000, 0, 0, 0));

        Assert.True(estimate.IsPriced);
        Assert.Equal(4m + 10m, estimate.ValueEur);
    }

    [Fact]
    public void Prices_cache_writes_and_reads_with_standard_multipliers()
    {
        // Sonnet 5: input $2 -> 5m write $2.50, 1h write $4, read $0.20.
        var estimate = UsdPricing.Estimate("claude-sonnet-5", new TokenCounts(0, 0, 1_000_000, 1_000_000, 1_000_000));

        Assert.Equal(2.5m + 4m + 0.2m, estimate.ValueEur);
    }

    [Fact]
    public void Uses_model_specific_cache_read_price()
    {
        // Fable 5.1 publishes $0.25 cache reads instead of 10% of $10.
        var estimate = UsdPricing.Estimate("claude-fable-5-1", new TokenCounts(0, 0, 0, 0, 1_000_000));

        Assert.Equal(0.25m, estimate.ValueEur);
    }

    [Fact]
    public void Converts_usd_to_eur()
    {
        var pricing = new ModelPricingService(new PricingOverrides { UsdToEur = 0.5m });

        Assert.Equal(2m, pricing.Estimate("claude-opus-5-5", new TokenCounts(1_000_000, 0, 0, 0, 0)).ValueEur);
    }

    [Theory]
    [InlineData("claude-haiku-4-5-20251001", 1)]
    [InlineData("claude-sonnet-4-20250514", 3)]
    [InlineData("claude-opus-4-1-20250805", 15)]
    [InlineData("gpt-5-2025-08-07", 1.25)]
    [InlineData("anthropic.claude-sonnet-4-5-20250929-v1:0", 3)]
    [InlineData("CLAUDE-OPUS-5", 5)]
    public void Resolves_dated_and_decorated_model_ids(string model, double inputPrice)
    {
        var price = UsdPricing.FindPrice(model);

        Assert.NotNull(price);
        Assert.Equal((decimal)inputPrice, price.Input);
    }

    [Theory]
    [InlineData("gpt-6-astra")]
    [InlineData("claude-opus-5-7")]
    [InlineData("gpt-5.2")]
    [InlineData("")]
    public void Unknown_models_stay_unpriced_instead_of_guessed(string model)
    {
        var estimate = UsdPricing.Estimate(model, new TokenCounts(1000, 1000, 0, 0, 0));

        Assert.False(estimate.IsPriced);
        Assert.Equal(0m, estimate.ValueEur);
    }

    [Fact]
    public void Overrides_take_precedence_over_built_in_prices()
    {
        var pricing = new ModelPricingService(new PricingOverrides
        {
            UsdToEur = 1m,
            Models = { ["gpt-6-astra"] = new ModelPrice { Input = 2m, Output = 8m } },
        });

        Assert.Equal(10m, pricing.Estimate("gpt-6-astra", new TokenCounts(1_000_000, 1_000_000, 0, 0, 0)).ValueEur);
    }

    [Fact]
    public void Broken_pricing_file_falls_back_to_built_in_prices()
    {
        var path = Path.Combine(TestData.TempDirectory(), "pricing.json");
        File.WriteAllText(path, "{ not json");

        var pricing = ModelPricingService.Load(path);

        Assert.Equal(ModelPricingService.DefaultUsdToEur, pricing.UsdToEur);
        Assert.True(pricing.IsKnownModel("claude-opus-5-5"));
    }

    [Fact]
    public void Zero_tokens_cost_nothing_even_for_unknown_models()
    {
        Assert.Equal(new PriceEstimate(0m, true), UsdPricing.Estimate("mystery", TokenCounts.Zero));
    }
}
