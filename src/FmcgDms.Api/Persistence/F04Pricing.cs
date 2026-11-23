using System.Numerics;

namespace FmcgDms.Api.Persistence;

public sealed record F04PriceInput(string Sku, decimal Quantity, decimal QuoteUnitPrice, decimal TaxRate);
public sealed record F04PriceAmount(long Net, long Tax, long Gross);
public sealed record F04PricedLine(string Sku, long Base, long Discount, long Net, long Tax, long Gross);

public static class F04Pricing
{
    private const long AmountLimit = 999999999999999;

    public static IReadOnlyList<F04PricedLine> Calculate(IReadOnlyList<F04PriceInput> lines, long discount, bool inclusive)
    {
        if (lines.Count is < 1 or > 24 || lines.Select(line => line.Sku).Distinct(StringComparer.Ordinal).Count() != lines.Count)
            throw new ArgumentException("A document must contain up to 24 unique source SKUs.", nameof(lines));
        long[] bases = lines.Select(Basis).ToArray();
        long totalBasis = bases.Sum();
        if (discount < 0 || discount > totalBasis)
            throw new ArgumentOutOfRangeException(nameof(discount), "Discount must fit the document amount.");
        var allocated = new long[lines.Count];
        if (discount > 0)
        {
            var remainders = new BigInteger[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                var share = BigInteger.DivRem((BigInteger)discount * bases[i], totalBasis, out remainders[i]);
                allocated[i] = (long)share;
            }
            int residual = (int)(discount - allocated.Sum());
            foreach (int i in Enumerable.Range(0, lines.Count).OrderByDescending(i => remainders[i])
                         .ThenBy(i => lines[i].Sku, StringComparer.Ordinal).Take(residual))
                allocated[i]++;
        }

        var result = new F04PricedLine[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            long discounted = bases[i] - allocated[i];
            decimal rate = lines[i].TaxRate;
            long net = inclusive ? Round(discounted / (1 + rate)) : discounted;
            long tax = inclusive ? discounted - net : Round(net * rate);
            long gross = net + tax;
            if (gross > AmountLimit)
                throw new ArgumentOutOfRangeException(nameof(lines), "Gross exceeds the posting range.");
            result[i] = new F04PricedLine(lines[i].Sku, bases[i], allocated[i], net, tax, gross);
        }
        return result;
    }

    public static F04PriceAmount Portion(long net, long gross, decimal totalQuantity, decimal priorQuantity,
        decimal quantity)
    {
        if (net < 0 || gross < net || gross > AmountLimit || totalQuantity <= 0 || priorQuantity < 0
            || quantity <= 0 || quantity > totalQuantity - priorQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Portion must fit its source.");
        long units = QuantityUnits(totalQuantity);
        long before = QuantityUnits(priorQuantity);
        long after = QuantityUnits(priorQuantity + quantity);
        long Delta(long amount) => Cumulative(amount, after, units) - Cumulative(amount, before, units);
        long netPart = Delta(net);
        long taxPart = Delta(gross - net);
        return new F04PriceAmount(netPart, taxPart, netPart + taxPart);
    }

    private static long QuantityUnits(decimal quantity)
    {
        if (quantity > 999999999999999.999m || decimal.Round(quantity, 3) != quantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity exceeds the base-unit scale.");
        return (long)(quantity * 1000);
    }

    private static long Cumulative(long amount, long part, long whole)
    {
        var quotient = BigInteger.DivRem((BigInteger)amount * part, whole, out var remainder);
        return (long)quotient + (remainder * 2 >= whole ? 1 : 0);
    }

    private static long Basis(F04PriceInput line)
    {
        if (string.IsNullOrWhiteSpace(line.Sku) || line.Quantity <= 0 || line.QuoteUnitPrice < 0
            || line.TaxRate is < 0 or > 1 || decimal.Round(line.QuoteUnitPrice, 4) != line.QuoteUnitPrice
            || (line.QuoteUnitPrice > 0 && line.Quantity > AmountLimit / line.QuoteUnitPrice))
            throw new ArgumentException("Invalid line amount or tax rate.", nameof(line));
        return Round(line.Quantity * line.QuoteUnitPrice);
    }

    private static long Round(decimal value) => (long)decimal.Round(value, 0, MidpointRounding.AwayFromZero);
}
