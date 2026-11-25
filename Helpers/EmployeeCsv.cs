using System.Text;

namespace nexthire_api.Helpers;

/// <summary>Minimal RFC 4180 CSV reader/writer for Core HR import and export.</summary>
public static class EmployeeCsv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text)) return rows;

        if (text[0] == '\uFEFF') text = text[1..];

        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;
                case '\r':
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (!(row.Count == 1 && row[0].Length == 0))
                        rows.Add(row);
                    row = new List<string>();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    i++;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (!(row.Count == 1 && row[0].Length == 0))
                rows.Add(row);
        }

        return rows;
    }

    /// <summary>Escape a single cell, quoting as needed and neutralizing spreadsheet formula injection.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var v = value;
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            v = "'" + v;

        if (v.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            return "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return v;
    }

    public static string Line(IEnumerable<string?> cells) => string.Join(",", cells.Select(Escape));

    /// <summary>Normalize a header name for lookups: lowercase, letters and digits only.</summary>
    public static string NormalizeHeader(string header) =>
        new string(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
