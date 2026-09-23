using System.Globalization;
using System.Text.RegularExpressions;

namespace VerifactuShopify;

// The connector's own invoice series, "{prefix}-{yyyy}-{nnnnnn}": shared by F1 and F2, restarted
// every calendar year (as the shop's manual numbering already does, see #3), and sortable as text.
// The AEAT is the source of truth for the last number used, like it is for the chain (#12): the
// caller collects the NumSerie of this SIF's registros and Next picks up from the highest one.
public sealed partial class InvoiceSeries
{
    readonly Regex _pattern;

    public InvoiceSeries(string prefix)
    {
        if (!PrefixPattern().IsMatch(prefix))
            throw new ArgumentException($"The invoice series prefix must be letters and digits only: '{prefix}'.", nameof(prefix));

        Prefix = prefix;
        _pattern = new Regex($@"^{prefix}-(?<year>\d{{4}})-(?<number>\d{{6,}})$", RegexOptions.CultureInvariant);
    }

    public string Prefix { get; }

    public string Format(int year, int number) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}-{year}-{number:D6}");

    // Invoice numbers from anything else (M1 test registros, another series) are ignored.
    public int? NumberIn(string numSerie, int year) =>
        _pattern.Match(numSerie) is { Success: true } match &&
        int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) == year
            ? int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture)
            : null;

    // lastUsedBefore seeds a year the connector didn't start in January: the last number the
    // shop used outside the connector that year (in production, its last manual invoice).
    public int Next(IEnumerable<string> numSeries, int year, int lastUsedBefore = 0) =>
        numSeries.Select(numSerie => NumberIn(numSerie, year)).Append(lastUsedBefore).Max(n => n ?? 0) + 1;

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex PrefixPattern();
}
