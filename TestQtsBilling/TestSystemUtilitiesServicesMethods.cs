using Bogus;
using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestSystemUtilitiesServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void sync_all_collections()
    {
        // Arrange
        var service = new SystemUtilitiesService();

        // Act
        service.SyncAllCollections();

        // Assert
        output.LogResponse("SyncAllCollections executed successfully");
    }

    [Fact]
    public void sync_local_with_cloud()
    {
        // Arrange
        var service = new SystemUtilitiesService();

        // Act
        service.SyncLocalWithCloud();

        // Assert
        output.LogResponse("SyncLocalWithCloud executed successfully");
    }

    [Fact]
    public void sync_remote_with_local()
    {
        // Arrange
        var service = new SystemUtilitiesService();

        // Act
        service.SyncRemoteWithLocal();

        // Assert
        output.LogResponse("SyncRemoteWithLocal executed successfully");
    }

    [Fact]
    public async Task sync_bidirectionally()
    {
        // Arrange
        var service = new SystemUtilitiesService();

        // Act
        await service.SyncBidirectionally();

        // Assert
        output.LogResponse("SyncBidirectionally executed successfully");
    }

    [Fact]
    public void audit_log()
    {
        // Arrange
        var service = new SystemUtilitiesService();
        var message = "Test audit log message";

        // Act
        service.AuditLog(message);

        // Assert
        output.LogResponse("AuditLog executed successfully");
    }

    [Fact]
    public void update_system_configuration()
    {
        // Arrange
        var service = new SystemUtilitiesService();
        var config = new Faker<SystemConfiguration>()
            .RuleFor(c => c.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(c => c.TaxRate, f => f.Random.Decimal(0, 100))
            .RuleFor(c => c.DefaultSettings, f => new Dictionary<string, string>
            {
                { "Setting1", f.Random.Word() },
                { "Setting2", f.Random.Word() }
            })
            .Generate();

        // Act
        var result = service.UpdateSystemConfiguration(config);

        // Assert
        Assert.True(result);
        output.LogResponse("UpdateSystemConfiguration executed successfully");
    }

    [Fact]
    public void backup_data()
    {
        // Arrange
        var service = new SystemUtilitiesService();
        var backup = new Faker<Backup>()
            .RuleFor(b => b.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(b => b.BackupDate, f => f.Date.Past())
            .RuleFor(b => b.BackupFilePath, f => f.System.FilePath())
            .RuleFor(b => b.Description, f => f.Lorem.Sentence())
            .Generate();

        // Act
        var result = service.BackupData(backup);

        // Assert
        Assert.True(result);
        output.LogResponse("BackupData executed successfully");
    }

    [Fact]
    public void restore_data()
    {
        // Arrange
        var service = new SystemUtilitiesService();
        var backup = new Faker<Backup>()
            .RuleFor(b => b.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(b => b.BackupDate, f => f.Date.Past())
            .RuleFor(b => b.BackupFilePath, f => f.System.FilePath())
            .RuleFor(b => b.Description, f => f.Lorem.Sentence())
            .Generate();
        service.BackupData(backup);

        // Act
        var result = service.RestoreData(backup.Id);

        // Assert
        Assert.True(result);
        output.LogResponse("RestoreData executed successfully");
    }

    [Fact]
    public void search_by_text()
    {
        // Arrange
        var service = new SystemUtilitiesService();
        var collection = new MongoClient(GlobalConstants.GetRemoteConnectionString())
            .GetDatabase("qtsBilling")
            .GetCollection<BsonDocument>("TestCollection");
        var searchText = "test";

        // Act
        var result = service.Search(searchText, collection);

        // Assert
        Assert.NotNull(result);
        output.LogResponse("Search executed successfully");
    }
}
