using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestJournalEntryServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void create_journal_entry()
    {
        // Arrange
        var journalEntry = new Faker<Transaction>()
            .RuleFor(j => j.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(j => j.Date, f => f.Date.Past())
            .RuleFor(j => j.Description, f => f.Lorem.Sentence())
            .RuleFor(j => j.Entries, f =>
            [
                new TransactionEntry
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    AccountId = ObjectId.GenerateNewId().ToString(),
                    Amount = f.Finance.Amount(),
                    Type = f.PickRandom<EntryType>()
                }
            ])
            .Generate();

        var service = new JournalEntryService();

        // Act
        var result = service.CreateJournalEntry(journalEntry);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        var isDeleted = DeleteJournalEntry_ExistingJournalEntry_ReturnsTrue(journalEntry.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void update_journal_entry()
    {
        // Arrange
        var journalEntry = new Faker<Transaction>()
            .RuleFor(j => j.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(j => j.Date, f => f.Date.Past())
            .RuleFor(j => j.Description, f => f.Lorem.Sentence())
            .RuleFor(j => j.Entries, f =>
            [
                new TransactionEntry
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    AccountId = ObjectId.GenerateNewId().ToString(),
                    Amount = f.Finance.Amount(),
                    Type = f.PickRandom<EntryType>()
                }
            ])
            .Generate();

        var service = new JournalEntryService();
        service.CreateJournalEntry(journalEntry);

        // Act
        journalEntry.Description = "Updated Description";
        var result = service.UpdateJournalEntry(journalEntry);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Clean up
        var isDeleted = DeleteJournalEntry_ExistingJournalEntry_ReturnsTrue(journalEntry.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void delete_journal_entry()
    {
        // Arrange
        var journalEntry = new Faker<Transaction>()
            .RuleFor(j => j.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(j => j.Date, f => f.Date.Past())
            .RuleFor(j => j.Description, f => f.Lorem.Sentence())
            .RuleFor(j => j.Entries, f =>
            [
                new TransactionEntry
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    AccountId = ObjectId.GenerateNewId().ToString(),
                    Amount = f.Finance.Amount(),
                    Type = f.PickRandom<EntryType>()
                }
            ])
            .Generate();

        var service = new JournalEntryService();
        service.CreateJournalEntry(journalEntry);

        // Act
        var result = service.DeleteJournalEntry(journalEntry.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void get_all_journal_entries()
    {
        // Arrange
        var service = new JournalEntryService();

        // Act
        var result = service.GetAllJournalEntries();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void get_journal_entry_by_id()
    {
        // Arrange
        var journalEntry = new Faker<Transaction>()
            .RuleFor(j => j.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(j => j.Date, f => f.Date.Past())
            .RuleFor(j => j.Description, f => f.Lorem.Sentence())
            .RuleFor(j => j.Entries, f =>
            [
                new TransactionEntry
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    AccountId = ObjectId.GenerateNewId().ToString(),
                    Amount = f.Finance.Amount(),
                    Type = f.PickRandom<EntryType>()
                }
            ])
            .Generate();

        var service = new JournalEntryService();
        service.CreateJournalEntry(journalEntry);

        // Act
        var result = service.GetJournalEntryById(journalEntry.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);

        // Clean up
        var isDeleted = DeleteJournalEntry_ExistingJournalEntry_ReturnsTrue(journalEntry.Id);
        Assert.True(isDeleted);
    }

    private bool DeleteJournalEntry_ExistingJournalEntry_ReturnsTrue(string id)
    {
        var service = new JournalEntryService();
        var result = service.DeleteJournalEntry(id);
        output.LogResponse("Journal entry with id " + id + " deleted successfully");
        return result;
    }
}
