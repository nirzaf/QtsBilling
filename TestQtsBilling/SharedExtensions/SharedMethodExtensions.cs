using Newtonsoft.Json;
using Xunit.Abstractions;

namespace TestQtsBilling.SharedExtensions;

public static class SharedMethodExtensions
{
    public static void LogResponse(this ITestOutputHelper output, object? response)
    {
        switch (response)
        {
            case null:
                output.WriteLine("Response is null");
                return;
            case string strResponse when string.IsNullOrEmpty(strResponse):
                output.WriteLine("No response received");
                return;
            default:
            {
                string jsonResponse = JsonConvert.SerializeObject(response, Formatting.Indented);
                output.WriteLine(jsonResponse);
                break;
            }
        }
    }

    public static void LogResponse(this ITestOutputHelper output, string? response)
    {
        if (string.IsNullOrEmpty(response))
        {
            output.WriteLine("No response received");
            return; 
        }

        output.WriteLine(response);
    }
}