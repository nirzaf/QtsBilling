using Bogus;
using MongoDB.Bson;
using QtsModels;
using QtsServices;
using TestQtsBilling.SharedExtensions;
using Xunit.Abstractions;

namespace TestQtsBilling;

public class TestUserManagementServicesMethods(ITestOutputHelper output)
{
    [Fact]
    public void Login_ValidCredentials_ReturnsTrue()
    {
        // Arrange
        var user = new Faker<User>()
            .RuleFor(u => u.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(u => u.Email, f => f.Internet.Email())
            .RuleFor(u => u.Password, f => f.Internet.Password())
            .Generate();

        var service = new UserManagementService();
        service.AddUser(user);

        // Act
        var result = service.Login(user.Email, user.Password);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Cleanup
        service.DeleteUser(user.Id);
    }

    [Fact]
    public void Login_InvalidCredentials_ReturnsFalse()
    {
        // Arrange
        var user = new Faker<User>()
            .RuleFor(u => u.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(u => u.Email, f => f.Internet.Email())
            .RuleFor(u => u.Password, f => f.Internet.Password())
            .Generate();

        var service = new UserManagementService();
        service.AddUser(user);

        // Act
        var result = service.Login(user.Email, "wrongpassword");
        output.LogResponse(result);

        // Assert
        Assert.False(result);

        // Cleanup
        service.DeleteUser(user.Id);
    }

    [Fact]
    public void Logout_ValidEmail_ReturnsTrue()
    {
        // Arrange
        var user = new Faker<User>()
            .RuleFor(u => u.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(u => u.Email, f => f.Internet.Email())
            .RuleFor(u => u.Password, f => f.Internet.Password())
            .Generate();

        var service = new UserManagementService();
        service.AddUser(user);

        // Act
        var result = service.Logout(user.Email);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Cleanup
        service.DeleteUser(user.Id);
    }

    [Fact]
    public void AddUser_ValidUser_ReturnsTrue()
    {
        // Arrange
        var user = new Faker<User>()
            .RuleFor(u => u.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(u => u.Email, f => f.Internet.Email())
            .RuleFor(u => u.Password, f => f.Internet.Password())
            .Generate();

        var service = new UserManagementService();

        // Act
        var result = service.AddUser(user);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Cleanup
        service.DeleteUser(user.Id);
    }

    [Fact]
    public void UpdateUser_ValidUser_ReturnsTrue()
    {
        // Arrange
        var user = new Faker<User>()
            .RuleFor(u => u.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(u => u.Email, f => f.Internet.Email())
            .RuleFor(u => u.Password, f => f.Internet.Password())
            .Generate();

        var service = new UserManagementService();
        service.AddUser(user);

        var updatedUser = new Faker<User>()
            .RuleFor(u => u.Email, f => f.Internet.Email())
            .RuleFor(u => u.Password, f => f.Internet.Password())
            .Generate();

        // Act
        var result = service.UpdateUser(updatedUser, user.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Cleanup
        service.DeleteUser(user.Id);
    }

    [Fact]
    public void DeleteUser_ValidUser_ReturnsTrue()
    {
        // Arrange
        var user = new Faker<User>()
            .RuleFor(u => u.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(u => u.Email, f => f.Internet.Email())
            .RuleFor(u => u.Password, f => f.Internet.Password())
            .Generate();

        var service = new UserManagementService();
        service.AddUser(user);

        // Act
        var result = service.DeleteUser(user.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void DeactivateUser_ValidUser_ReturnsTrue()
    {
        // Arrange
        var user = new Faker<User>()
            .RuleFor(u => u.Id, _ => ObjectId.GenerateNewId().ToString())
            .RuleFor(u => u.Email, f => f.Internet.Email())
            .RuleFor(u => u.Password, f => f.Internet.Password())
            .Generate();

        var service = new UserManagementService();
        service.AddUser(user);

        // Act
        var result = service.DeactivateUser(user.Id);
        output.LogResponse(result);

        // Assert
        Assert.True(result);

        // Cleanup
        service.DeleteUser(user.Id);
    }
}
