using System.Text.Json;
using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed class App02DevF04PricingTests
{
    [Theory]
    [InlineData("EXCLUSIVE")]
    [InlineData("INCLUSIVE")]
    public void F04_01_02_price_rows_match_locked_AC19_oracle(string mode)
    {
        using var golden = Golden();
        JsonElement scenario = golden.RootElement.GetProperty("AC-19");
        bool inclusive = mode == "INCLUSIVE";
        F04PriceInput[] inputs = [
            new("P", 3, inclusive ? 72000m : 66666.6667m, .08m),
            new("Q", 1, inclusive ? 110000m : 100000m, .10m)
        ];
        long discount = scenario.GetProperty("discount").GetInt64();
        var lines = F04Pricing.Calculate(inputs.Reverse().ToArray(), discount, inclusive);

        foreach (JsonElement expected in scenario.GetProperty(mode).EnumerateArray())
        {
            var line = Assert.Single(lines, x => x.Sku == expected[0].GetString());
            Assert.Equal(expected[1].GetInt64(), line.Base);
            Assert.Equal(expected[2].GetInt64(), line.Discount);
            Assert.Equal(expected[3].GetInt64(), line.Net);
            Assert.Equal(expected[4].GetInt64(), line.Tax);
            Assert.Equal(expected[5].GetInt64(), line.Gross);
        }
        Assert.Equal(discount, lines.Sum(line => line.Discount));
        JsonElement total = scenario.GetProperty("totals").GetProperty(mode);
        Assert.Equal(total[0].GetInt64(), lines.Sum(line => line.Net));
        Assert.Equal(total[1].GetInt64(), lines.Sum(line => line.Tax));
        Assert.Equal(total[2].GetInt64(), lines.Sum(line => line.Gross));
    }

    [Fact]
    public void F04_03_prorated_net_tax_gross_preserve_locked_residual()
    {
        using var golden = Golden();
        var oracle = golden.RootElement.GetProperty("AC-19");
        var p = oracle.GetProperty("EXCLUSIVE")[0];
        var expected = oracle.GetProperty("returnParts").EnumerateArray().ToArray();
        for (int i = 0; i < expected.Length; i++)
        {
            var actual = F04Pricing.Portion(p[3].GetInt64(), p[5].GetInt64(), 3, i, 1);
            Assert.Equal((expected[i][0].GetInt64(), expected[i][1].GetInt64(), expected[i][2].GetInt64()),
                (actual.Net, actual.Tax, actual.Gross));
        }
    }

    [Fact]
    public void F04_03_exact_half_is_not_lost_to_decimal_division()
    {
        var amount = F04Pricing.Portion(21, 21, 42, 0, 1);
        Assert.Equal(new F04PriceAmount(1, 0, 1), amount);
    }

    [Fact]
    public void F04_04_discount_remainder_tie_uses_stable_source_order()
    {
        var actual = F04Pricing.Calculate([new("Q", 1, 1, 0), new("P", 1, 1, 0)], 1, inclusive: false);
        Assert.Equal(1, Assert.Single(actual, x => x.Sku == "P").Discount);
        Assert.Equal(0, Assert.Single(actual, x => x.Sku == "Q").Discount);
        Assert.Throws<ArgumentOutOfRangeException>(() => F04Pricing.Calculate([new("P", 1, 1, 0)], 2, false));
    }

    private static JsonDocument Golden()
    {
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
        {
            string path = Path.Combine(parent.FullName, "business", "reference-data", "expected-results", "golden-oracles.json");
            if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllText(path));
        }
        throw new FileNotFoundException("Locked AC-19 fixture missing.");
    }
}
