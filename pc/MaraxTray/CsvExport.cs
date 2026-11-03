using System.Globalization;
using System.Text;

namespace MaraxTray;

// Writes CSV the way a German Excel opens it directly: semicolons, decimal commas, UTF-8 with BOM.
internal static class CsvExport
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static async Task WriteAsync(Stream output, string[] headers, IEnumerable<object?[]> rows)
    {
        await using var writer = new StreamWriter(output, Utf8WithBom, leaveOpen: true);
        await writer.WriteLineAsync(string.Join(';', headers.Select(Cell)));
        foreach (object?[] row in rows)
        {
            await writer.WriteLineAsync(string.Join(';', row.Select(Cell)));
        }
    }

    public static string Time(long? ms) => ms is long value
        ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", German)
        : "";

    private static string Cell(object? value) => value switch
    {
        null or DBNull => "",
        bool flag => flag ? "ja" : "nein",
        double number => number.ToString("0.0", German),
        string text => Text(text),
        _ => Convert.ToString(value, German) ?? "",
    };

    private static string Text(string text)
    {
        // Excel would run a cell like "+1.22,112" as a formula
        if (text.Length > 0 && "=+-@".Contains(text[0]))
        {
            text = "'" + text;
        }
        return text.Contains(';') || text.Contains('"') || text.Contains('\n')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}
