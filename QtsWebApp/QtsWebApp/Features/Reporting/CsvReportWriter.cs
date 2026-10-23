using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace QtsWebApp.Features.Reporting;

/// <summary>
/// Streams report records as a UTF-8 BOM CSV table with a stable Section/Item/Field/Label/Value schema. The machine
/// column name stays exactly as the contract spells it, so a consumer can key on it, and the label beside it is the
/// words a person reads. Money keeps the scale the server posted it at and quantities keep their six decimals; a
/// cell that could be read as a spreadsheet formula is neutralised before it is written, and the export stops at a
/// bounded row count rather than streaming an unbounded table (specification task 33, recipe step 3).
/// </summary>
internal static class CsvReportWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Ceiling on emitted rows. A report that needs more has been asked the wrong question.</summary>
    internal const int MaxRows = 200_000;

    public static IResult Create(object report, string fileName) => new CsvReportResult(report, fileName);

    private static async Task WriteAsync(Stream stream, object report, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, Utf8WithBom, bufferSize: 4096, leaveOpen: true);
        await writer.WriteLineAsync("\"Section\",\"Item\",\"Field\",\"Label\",\"Value\"").ConfigureAwait(false);
        await WriteObjectAsync(writer, report, "summary", string.Empty, new RowCounter(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One counter shared by the whole stream, so a report cannot grow past the ceiling row by row.</summary>
    private sealed class RowCounter
    {
        public int Rows { get; private set; }

        public void Add(int count = 1)
        {
            Rows += count;
            if (Rows > MaxRows)
            {
                throw new InvalidOperationException($"A CSV export carries at most {MaxRows} rows.");
            }
        }
    }

    private static async Task WriteObjectAsync(
        StreamWriter writer,
        object? value,
        string section,
        string item,
        RowCounter rows,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null)
        {
            await WriteRowAsync(writer, section, item, "Value", null, rows).ConfigureAwait(false);
            return;
        }

        if (value is IEnumerable sequence && value is not string)
        {
            var index = 0;
            foreach (var element in sequence)
            {
                await WriteObjectAsync(writer, element, section, index.ToString(CultureInfo.InvariantCulture), rows, cancellationToken).ConfigureAwait(false);
                index++;
            }

            return;
        }

        if (IsScalar(value.GetType()))
        {
            await WriteRowAsync(writer, section, item, "Value", value, rows).ConfigureAwait(false);
            return;
        }

        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fieldValue = property.GetValue(value);
            if (fieldValue is IEnumerable nested && fieldValue is not string)
            {
                var index = 0;
                foreach (var element in nested)
                {
                    await WriteObjectAsync(
                        writer,
                        element,
                        property.Name,
                        index.ToString(CultureInfo.InvariantCulture),
                        rows,
                        cancellationToken).ConfigureAwait(false);
                    index++;
                }
            }
            else if (fieldValue is not null && !IsScalar(fieldValue.GetType()))
            {
                await WriteObjectAsync(writer, fieldValue, property.Name, item, rows, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteRowAsync(writer, section, item, property.Name, fieldValue, rows).ConfigureAwait(false);
            }
        }
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(Guid)
            || type == typeof(DateOnly)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeOnly)
            || type == typeof(TimeSpan);
    }

    private static async Task WriteRowAsync(StreamWriter writer, string section, string item, string field, object? value, RowCounter rows)
    {
        var line = string.Join(',',
        [
            Escape(section),
            Escape(item),
            Escape(field),
            Escape(Humanize(field)),
            Escape(Format(value)),
        ]);
        rows.Add();
        await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    /// <summary>
    /// The words a person reads beside the machine column. The field name itself is never replaced: a consumer keys
    /// on it, and a label that drifts from the contract would make the export agree with nobody.
    /// </summary>
    internal static string Humanize(string field)
    {
        if (field.Length == 0)
        {
            return field;
        }

        var builder = new StringBuilder(field.Length + 8);
        builder.Append(char.ToUpperInvariant(field[0]));
        for (var index = 1; index < field.Length; index++)
        {
            var current = field[index];
            if (char.IsUpper(current)
                && (char.IsLower(field[index - 1])
                    || (index + 1 < field.Length && char.IsLower(field[index + 1]) && char.IsUpper(field[index - 1]))))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static string Format(object? value)
        => value switch
        {
            null => string.Empty,
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };

    private static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private sealed class CsvReportResult(object report, string fileName) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "text/csv; charset=utf-8";
            httpContext.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Path.GetFileName(fileName)}\"";
            await WriteAsync(httpContext.Response.Body, report, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
