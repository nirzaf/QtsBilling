using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace QtsWebApp.Features.Reporting;

/// <summary>
/// Streams report records as a UTF-8 BOM CSV table with a stable Section/Item/Field/Value schema. The
/// four-column form supports reports with multiple detail collections without losing their section names.
/// </summary>
internal static class CsvReportWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static IResult Create(object report, string fileName) => new CsvReportResult(report, fileName);

    private static async Task WriteAsync(Stream stream, object report, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, Utf8WithBom, bufferSize: 4096, leaveOpen: true);
        await writer.WriteLineAsync("\"Section\",\"Item\",\"Field\",\"Value\"").ConfigureAwait(false);
        await WriteObjectAsync(writer, report, "summary", string.Empty, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteObjectAsync(
        StreamWriter writer,
        object? value,
        string section,
        string item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null)
        {
            await WriteRowAsync(writer, section, item, "Value", null).ConfigureAwait(false);
            return;
        }

        if (value is IEnumerable sequence && value is not string)
        {
            var index = 0;
            foreach (var element in sequence)
            {
                await WriteObjectAsync(writer, element, section, index.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                index++;
            }

            return;
        }

        if (IsScalar(value.GetType()))
        {
            await WriteRowAsync(writer, section, item, "Value", value).ConfigureAwait(false);
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
                        cancellationToken).ConfigureAwait(false);
                    index++;
                }
            }
            else if (fieldValue is not null && !IsScalar(fieldValue.GetType()))
            {
                await WriteObjectAsync(writer, fieldValue, property.Name, item, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteRowAsync(writer, section, item, property.Name, fieldValue).ConfigureAwait(false);
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

    private static async Task WriteRowAsync(StreamWriter writer, string section, string item, string field, object? value)
    {
        var line = string.Join(',',
        [
            Escape(section),
            Escape(item),
            Escape(field),
            Escape(Format(value)),
        ]);
        await writer.WriteLineAsync(line).ConfigureAwait(false);
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
