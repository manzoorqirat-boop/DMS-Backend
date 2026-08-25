using System.Globalization;
using System.Text;

namespace Dms.Api;

/// <summary>
/// Minimal RFC 4180 CSV writer for register and audit exports.
/// <para>
/// Hand-rolled rather than a package because the requirement is one function and the risk is
/// in the escaping, which is worth being able to read. Handing an inspector a file whose
/// columns silently shifted because a document title contained a comma is not a failure mode
/// worth taking on a dependency to avoid — it's one worth seeing the code for.
/// </para>
/// </summary>
public static class Csv
{
    public static byte[] Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();

        // UTF-8 BOM. Excel is the tool these exports are opened in, and without it Excel
        // mis-reads non-ASCII — which in a multi-site pharma register means mangled names.
        builder.Append('\uFEFF');

        builder.AppendLine(string.Join(",", headers.Select(Escape)));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(Escape)));
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static string Field(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

    public static string Field(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public static string Field(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        // Leading =, +, - or @ make Excel treat the cell as a formula. A document title
        // starting with one is unlikely but a username or a free-text reason could be crafted
        // to, and a register that executes its own contents when opened is a real problem.
        var text = value.Length > 0 && value[0] is '=' or '+' or '-' or '@'
            ? "'" + value
            : value;

        return text.Any(c => c is ',' or '"' or '\n' or '\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}
