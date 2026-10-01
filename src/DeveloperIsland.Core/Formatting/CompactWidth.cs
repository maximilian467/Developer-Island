using System.Text;
using System.Text.RegularExpressions;

namespace DeveloperIsland.Core.Formatting;

/// <summary>
/// The widest text a changing compact value can take, so its slot keeps one width while the value
/// ticks: "CPU 9%" and "CPU 100%" both reserve "CPU 000%", "376k" and "1.12M" both "000.00M".
/// With tabular figures every digit is as wide as "0", so the template measures the real maximum.
/// </summary>
public static partial class CompactWidth
{
    [GeneratedRegex(@"\d+(?:[.,]\d+)?(?:(?<unit>[kMB])\b|(?=(?<suffix>[%°:])))?")]
    private static partial Regex Number();

    /// <summary>The reserve template for <paramref name="text"/>: its words, with each number at its widest.</summary>
    public static string Reserve(string text, string decimalSeparator = ".")
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return Number().Replace(text, match =>
        {
            var suffix = match.Groups["unit"].Success ? "M" : match.Groups["suffix"].Success ? match.Groups["suffix"].Value : string.Empty;
            var separator = match.Value.Contains(',') ? "," : match.Value.Contains('.') ? "." : decimalSeparator;
            return suffix switch
            {
                // Token counts switch between "999k" and "1.12M": reserve three digits and two decimals.
                "M" => "000" + separator + "00M",

                // Percentages and temperatures reach three digits ("100%", "100°").
                "%" or "°" => Zeros(match.Value, 3),

                // Clock parts and counts: at least two digits ("9:05" and "12:05", "in 5 min" and "in 45 min").
                _ => Zeros(match.Value, 2),
            };
        });
    }

    /// <summary>The number with every digit as "0" and its integer part padded to <paramref name="digits"/>.</summary>
    private static string Zeros(string number, int digits)
    {
        var builder = new StringBuilder(number.Length + digits);
        var separatorAt = number.IndexOfAny(['.', ',']);
        var integerLength = separatorAt < 0 ? number.Length : separatorAt;
        builder.Append('0', Math.Max(digits, integerLength));
        if (separatorAt >= 0)
        {
            builder.Append(number[separatorAt]).Append('0', number.Length - separatorAt - 1);
        }

        return builder.ToString();
    }
}
