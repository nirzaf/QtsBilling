using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestInvoiceServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void create_invoice()
    {
        // Arrange
        var invoice = new Faker<Invoice>()
            .RuleFor(i => i.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(i => i.CustomerId, f => f.Random.Guid().ToString())
            .RuleFor(i => i.Date, f => f.Date.Past())
            .RuleFor(i => i.Products, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(), Name = f.Commerce.ProductName(),
                    Quantity = f.Random.Int(1, 10)
                }
            ])
            .RuleFor(i => i.TotalAmount, f => f.Finance.Amount())
            .RuleFor(i => i.Status, f => f.PickRandom<InvoiceStatus>())
            .Generate();

        var service = new InvoiceService();
        service.CreateInvoice(invoice);

        // Act
        var result = service.GetInvoiceById(invoice.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(invoice.CustomerId, result.CustomerId);
        Assert.Equal(invoice.Date, result.Date);
        Assert.Equal(invoice.Products.Count, result.Products.Count);
        Assert.Equal(invoice.TotalAmount, result.TotalAmount);
        Assert.Equal(invoice.Status, result.Status);

        // delete the test invoice
        bool isDeleted = DeleteInvoice_ExistingInvoice_ReturnsTrue(invoice.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void update_invoice()
    {
        // Arrange
        var invoice = new Faker<Invoice>()
            .RuleFor(i => i.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(i => i.CustomerId, f => f.Random.Guid().ToString())
            .RuleFor(i => i.Date, f => f.Date.Past())
            .RuleFor(i => i.Products, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(), Name = f.Commerce.ProductName(),
                    Quantity = f.Random.Int(1, 10)
                }
            ])
            .RuleFor(i => i.TotalAmount, f => f.Finance.Amount())
            .RuleFor(i => i.Status, f => f.PickRandom<InvoiceStatus>())
            .Generate();

        var service = new InvoiceService();
        service.CreateInvoice(invoice);

        // Act
        var updatedInvoice = new Faker<Invoice>()
            .RuleFor(i => i.Id, _ => invoice.Id)
            .RuleFor(i => i.CustomerId, f => f.Random.Guid().ToString())
            .RuleFor(i => i.Date, f => f.Date.Past())
            .RuleFor(i => i.Products, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(), Name = f.Commerce.ProductName(),
                    Quantity = f.Random.Int(1, 10)
                }
            ])
            .RuleFor(i => i.TotalAmount, f => f.Finance.Amount())
            .RuleFor(i => i.Status, f => f.PickRandom<InvoiceStatus>())
            .Generate();

        service.UpdateInvoice(updatedInvoice);
        var result = service.GetInvoiceById(invoice.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(updatedInvoice.CustomerId, result.CustomerId);
        Assert.Equal(updatedInvoice.Date, result.Date);
        Assert.Equal(updatedInvoice.Products.Count, result.Products.Count);
        Assert.Equal(updatedInvoice.TotalAmount, result.TotalAmount);
        Assert.Equal(updatedInvoice.Status, result.Status);

        // delete the test invoice
        bool isDeleted = DeleteInvoice_ExistingInvoice_ReturnsTrue(invoice.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void delete_invoice()
    {
        // Arrange
        var invoice = new Faker<Invoice>()
            .RuleFor(i => i.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(i => i.CustomerId, f => f.Random.Guid().ToString())
            .RuleFor(i => i.Date, f => f.Date.Past())
            .RuleFor(i => i.Products, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(), Name = f.Commerce.ProductName(),
                    Quantity = f.Random.Int(1, 10)
                }
            ])
            .RuleFor(i => i.TotalAmount, f => f.Finance.Amount())
            .RuleFor(i => i.Status, f => f.PickRandom<InvoiceStatus>())
            .Generate();

        var service = new InvoiceService();
        service.CreateInvoice(invoice);

        // Act
        var result = service.DeleteInvoice(invoice.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void get_invoice_by_id()
    {
        // Arrange
        var invoice = new Faker<Invoice>()
            .RuleFor(i => i.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(i => i.CustomerId, f => f.Random.Guid().ToString())
            .RuleFor(i => i.Date, f => f.Date.Past())
            .RuleFor(i => i.Products, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(), Name = f.Commerce.ProductName(),
                    Quantity = f.Random.Int(1, 10)
                }
            ])
            .RuleFor(i => i.TotalAmount, f => f.Finance.Amount())
            .RuleFor(i => i.Status, f => f.PickRandom<InvoiceStatus>())
            .Generate();

        var service = new InvoiceService();
        service.CreateInvoice(invoice);

        // Act
        var result = service.GetInvoiceById(invoice.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(invoice.CustomerId, result.CustomerId);
        Assert.Equal(invoice.Date, result.Date);
        Assert.Equal(invoice.Products.Count, result.Products.Count);
        Assert.Equal(invoice.TotalAmount, result.TotalAmount);
        Assert.Equal(invoice.Status, result.Status);

        // delete the test invoice
        bool isDeleted = DeleteInvoice_ExistingInvoice_ReturnsTrue(invoice.Id);
        Assert.True(isDeleted);
    }

    [Fact]
    public void get_all_invoices()
    {
        // Arrange
        var invoice1 = new Faker<Invoice>()
            .RuleFor(i => i.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(i => i.CustomerId, f => f.Random.Guid().ToString())
            .RuleFor(i => i.Date, f => f.Date.Past())
            .RuleFor(i => i.Products, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(), Name = f.Commerce.ProductName(),
                    Quantity = f.Random.Int(1, 10)
                }
            ])
            .RuleFor(i => i.TotalAmount, f => f.Finance.Amount())
            .RuleFor(i => i.Status, f => f.PickRandom<InvoiceStatus>())
            .Generate();

        var invoice2 = new Faker<Invoice>()
            .RuleFor(i => i.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(i => i.CustomerId, f => f.Random.Guid().ToString())
            .RuleFor(i => i.Date, f => f.Date.Past())
            .RuleFor(i => i.Products, f =>
            [
                new Product
                {
                    Id = ObjectId.GenerateNewId().ToString(), Name = f.Commerce.ProductName(),
                    Quantity = f.Random.Int(1, 10)
                }
            ])
            .RuleFor(i => i.TotalAmount, f => f.Finance.Amount())
            .RuleFor(i => i.Status, f => f.PickRandom<InvoiceStatus>())
            .Generate();

        var service = new InvoiceService();
        service.CreateInvoice(invoice1);
        service.CreateInvoice(invoice2);

        // Act
        var result = service.GetAllInvoices();
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Count >= 2);

        // delete the test invoices
        bool isDeleted1 = DeleteInvoice_ExistingInvoice_ReturnsTrue(invoice1.Id);
        Assert.True(isDeleted1);

        bool isDeleted2 = DeleteInvoice_ExistingInvoice_ReturnsTrue(invoice2.Id);
        Assert.True(isDeleted2);
    }

    private bool DeleteInvoice_ExistingInvoice_ReturnsTrue(string id)
    {
        InvoiceService service = new();
        var result = service.DeleteInvoice(id);
        output.LogResponse("Invoice with id " + id + " deleted successfully");
        return result;
    }
}
