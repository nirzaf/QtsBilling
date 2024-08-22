using Bogus;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestCustomerServicesMethods(ITestOutputHelper output)
{

    [Fact]
    public void initializes_mongo_client_with_connection_string()
    {
        // Arrange
        var customerService = new CustomerService();

        // Act
        var mongoClient = customerService.GetMongoClient(GlobalConstants.GetRemoteConnectionString());

        // Assert
        Assert.NotNull(mongoClient);
        Assert.IsType<MongoClient>(mongoClient);
    }
    
    [Fact]
    public void get_customer_id_by_phone_valid_phone()
    {
        // Arrange
        var customer = new Faker<Customer>()
            .RuleFor(c => c.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(c => c.Name, f => f.Person.FullName)
            .RuleFor(c => c.Phone, f => f.Phone.PhoneNumber())
            .RuleFor(c => c.Address, f => f.Address.FullAddress())
            .RuleFor(c => c.Email, f => f.Person.Email)
            .Generate();

        var service = new CustomerService();
        service.AddCustomer(customer);
 
        // Act
        var result = service.GetCustomerIdByPhone(customer.Phone);
        output.LogResponse(result);
        
        // Assert
        Assert.Equal(customer.Id, result);

        // delete the test customer
        bool isDeleted =DeleteCustomer_ExistingCustomer_ReturnsTrue(customer.Id!);
        Assert.True(isDeleted);
    }
    
    [Fact]
    public void get_customer_id_by_phone_invalid_phone()
    {
        // Arrange
        var customer = new Faker<Customer>()
            .RuleFor(c => c.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(c => c.Name, f => f.Person.FullName)
            .RuleFor(c => c.Phone, f => f.Phone.PhoneNumber())
            .RuleFor(c => c.Address, f => f.Address.FullAddress())
            .RuleFor(c => c.Email, f => f.Person.Email)
            .Generate();

        var service = new CustomerService();
        service.AddCustomer(customer);

        // Act
        var result = service.GetCustomerIdByPhone("1234567890");
        output.LogResponse(result);

        // Assert
        Assert.Null(result);

        // delete the test customer
        bool isDeleted =DeleteCustomer_ExistingCustomer_ReturnsTrue(customer.Id!);
        Assert.True(isDeleted);
    }

    private bool DeleteCustomer_ExistingCustomer_ReturnsTrue(string id)
    {
        CustomerService service = new();
        var result = service.DeleteCustomer(id);
        output.LogResponse("Customer with id " + id + " deleted successfully");
        return result;
    }
    
    // test to check if customer is added, updated and deleted successfully
    [Fact]
    public void add_customer_update_customer_delete_customer()
    {
        // Arrange
        var customer = new Faker<Customer>()
            .RuleFor(c => c.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(c => c.Name, f => f.Person.FullName)
            .RuleFor(c => c.Phone, f => f.Phone.PhoneNumber())
            .RuleFor(c => c.Address, f => f.Address.FullAddress())
            .RuleFor(c => c.Email, f => f.Person.Email)
            .Generate();

        var service = new CustomerService();
        service.AddCustomer(customer);

        // Act
        var result = service.GetCustomerById(customer.Id);
        output.LogResponse(result);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(customer.Name, result.Name);
        Assert.Equal(customer.Phone, result.Phone);
        Assert.Equal(customer.Address, result.Address);
        Assert.Equal(customer.Email, result.Email);

        // update the customer
        var updatedCustomer = new Faker<Customer>()
            .RuleFor(c => c.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(c => c.Name, f => f.Person.FullName)
            .RuleFor(c => c.Phone, f => f.Phone.PhoneNumber())
            .RuleFor(c => c.Address, f => f.Address.FullAddress())
            .RuleFor(c => c.Email, f => f.Person.Email)
            .Generate();
        
        service.UpdateCustomer(updatedCustomer, customer.Id);
        var updatedCustomerResult = service.GetCustomerById(customer.Id);
        output.LogResponse(updatedCustomerResult);

        // Assert
        Assert.NotNull(updatedCustomerResult);
        Assert.Equal(updatedCustomer.Name, updatedCustomerResult.Name);
        Assert.Equal(updatedCustomer.Phone, updatedCustomerResult.Phone);
        Assert.Equal(updatedCustomer.Address, updatedCustomerResult.Address);
        Assert.Equal(updatedCustomer.Email, updatedCustomerResult.Email);
        
        // delete the test customer
        bool isDeleted =DeleteCustomer_ExistingCustomer_ReturnsTrue(customer.Id!);
        Assert.True(isDeleted);
    }
}