using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using QtsBilling.DataImport;
using QtsBilling.Persistence.Postgres;

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var arguments = new List<string>();

for (var index = 0; index < args.Length; index++)
{
    if (args[index].StartsWith("--", StringComparison.Ordinal))
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"The option '{args[index]}' needs a value.");
            return 2;
        }

        options[args[index][2..]] = args[++index];
    }
    else
    {
        arguments.Add(args[index]);
    }
}

if (arguments.Count != 1
    || arguments[0] is not ("validate" or "preview" or "apply"))
{
    Console.Error.WriteLine("usage: QtsBilling.DataImport <validate|preview|apply> --package <directory> [--user <guid>]");
    Console.Error.WriteLine("  the package is native; no legacy snapshot or source database is read.");
    return 2;
}

if (!options.TryGetValue("package", out var packageRoot) || string.IsNullOrWhiteSpace(packageRoot))
{
    Console.Error.WriteLine("--package <directory> is required.");
    return 2;
}

var keyId = Environment.GetEnvironmentVariable("QTS_NATIVE_IMPORT_KEY_ID");
var keyHex = Environment.GetEnvironmentVariable("QTS_NATIVE_IMPORT_HMAC_KEY");

if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(keyHex))
{
    Console.Error.WriteLine("QTS_NATIVE_IMPORT_KEY_ID and QTS_NATIVE_IMPORT_HMAC_KEY must name the approval trust.");
    return 2;
}

byte[] key;

try
{
    key = Convert.FromHexString(keyHex);
}
catch (FormatException)
{
    Console.Error.WriteLine("QTS_NATIVE_IMPORT_HMAC_KEY is not hexadecimal.");
    return 2;
}

if (key.Length < 16)
{
    Console.Error.WriteLine("QTS_NATIVE_IMPORT_HMAC_KEY is shorter than 16 bytes.");
    return 2;
}

var reader = new NativePackageReader();
var validation = await reader.ValidateAsync(packageRoot, new Dictionary<string, byte[]> { [keyId] = key });
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};

if (!validation.IsValid)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(validation.Report, jsonOptions));
    return 1;
}

var package = validation.Package!;

if (arguments[0] == "validate")
{
    Console.WriteLine(JsonSerializer.Serialize(validation.Report, jsonOptions));
    return 0;
}

if (arguments[0] == "preview")
{
    Console.WriteLine(JsonSerializer.Serialize(reader.Preview(package), jsonOptions));
    return 0;
}

if (!string.Equals(
        Environment.GetEnvironmentVariable("QTS_NATIVE_IMPORT_EXECUTE"),
        "1",
        StringComparison.Ordinal))
{
    Console.Error.WriteLine("apply requires QTS_NATIVE_IMPORT_EXECUTE=1.");
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable("QTS_POSTGRES_CONNECTION");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("QTS_POSTGRES_CONNECTION must name the target PostgreSQL database.");
    return 2;
}

Guid? userId = null;

if (options.TryGetValue("user", out var userText))
{
    if (!Guid.TryParse(userText, out var parsedUser))
    {
        Console.Error.WriteLine("--user must be a Guid.");
        return 2;
    }

    userId = parsedUser;
}

var dbOptions = new DbContextOptionsBuilder<BillingDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var db = new BillingDbContext(dbOptions);
var writer = new NativeImportWriter(reader);
var result = await writer.ApplyAsync(
    db,
    package,
    new NativeImportOptions
    {
        TargetBusinessId = package.Manifest.TargetBusinessId,
        UserId = userId,
    });

Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
return result.Committed ? 0 : 1;
