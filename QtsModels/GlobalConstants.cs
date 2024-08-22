namespace QtsModels;

public static class GlobalConstants
{
    public static string GetRemoteConnectionString()
    {
        return
            "mongodb+srv://mfmfazrin1986:7pJAvuhocx1caZFV@primarycluster.kdmpx.mongodb.net/?retryWrites=true&w=majority&appName=primaryCluster";
    }

    public static string GetLocalConnectionString()
    {
        return "mongodb://localhost:27017/qts-Billing";
    }
}