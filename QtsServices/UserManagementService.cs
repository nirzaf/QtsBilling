using MongoDB.Bson;
using MongoDB.Driver;
using QtsModels;

namespace QtsServices;


public class UserManagementService
{
    private readonly IMongoCollection<User> _usersCollection;

    public UserManagementService()
    {
        var mongoConnectionString = GlobalConstants.GetRemoteConnectionString();
        var mongoClient = new MongoClient(mongoConnectionString);
        var database = mongoClient.GetDatabase("qtsBilling");

        _usersCollection = database.GetCollection<User>("Users");
        EnsureUniqueEmail();
    }

    public bool Login(string email, string password)
    {
        try
        {
            var filter = Builders<User>.Filter.Eq(u => u.Email, email) &
                         Builders<User>.Filter.Eq(u => u.Password, password);
            var user = _usersCollection.Find(filter).FirstOrDefault();
            return user != null;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool Logout(string email)
    {
        try
        {
            var filter = Builders<User>.Filter.Eq(u => u.Email, email);
            var update = Builders<User>.Update.Set(u => u.RememberToken, null);
            var result = _usersCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool AddUser(User user)
    {
        try
        {
            user.Id = ObjectId.GenerateNewId().ToString();
            _usersCollection.InsertOne(user);
            return true;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool UpdateUser(User user, string id)
    {
        try
        {
            var filter = Builders<User>.Filter.Eq(u => u.Id, id);
            var update = Builders<User>.Update
                .Set(u => u.Surname, user.Surname)
                .Set(u => u.FirstName, user.FirstName)
                .Set(u => u.LastName, user.LastName)
                .Set(u => u.Username, user.Username)
                .Set(u => u.Email, user.Email)
                .Set(u => u.Password, user.Password)
                .Set(u => u.Language, user.Language)
                .Set(u => u.ContactNo, user.ContactNo)
                .Set(u => u.Address, user.Address)
                .Set(u => u.RememberToken, user.RememberToken)
                .Set(u => u.BusinessId, user.BusinessId)
                .Set(u => u.MaxSalesDiscountPercent, user.MaxSalesDiscountPercent)
                .Set(u => u.AllowLogin, user.AllowLogin)
                .Set(u => u.Status, user.Status)
                .Set(u => u.IsCmmnsAgnt, user.IsCmmnsAgnt)
                .Set(u => u.CmmnsPercent, user.CmmnsPercent)
                .Set(u => u.SelectedContacts, user.SelectedContacts)
                .Set(u => u.Dob, user.Dob)
                .Set(u => u.Gender, user.Gender)
                .Set(u => u.MaritalStatus, user.MaritalStatus)
                .Set(u => u.BloodGroup, user.BloodGroup)
                .Set(u => u.ContactNumber, user.ContactNumber)
                .Set(u => u.FbLink, user.FbLink)
                .Set(u => u.TwitterLink, user.TwitterLink)
                .Set(u => u.SocialMedia1, user.SocialMedia1)
                .Set(u => u.SocialMedia2, user.SocialMedia2)
                .Set(u => u.PermanentAddress, user.PermanentAddress)
                .Set(u => u.CurrentAddress, user.CurrentAddress)
                .Set(u => u.GuardianName, user.GuardianName)
                .Set(u => u.CustomField1, user.CustomField1)
                .Set(u => u.CustomField2, user.CustomField2)
                .Set(u => u.CustomField3, user.CustomField3)
                .Set(u => u.CustomField4, user.CustomField4)
                .Set(u => u.BankDetails, user.BankDetails)
                .Set(u => u.IdProofName, user.IdProofName)
                .Set(u => u.IdProofNumber, user.IdProofNumber)
                .Set(u => u.DeletedAt, user.DeletedAt)
                .Set(u => u.CreatedAt, user.CreatedAt)
                .Set(u => u.UpdatedAt, user.UpdatedAt);

            var result = _usersCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeleteUser(string id)
    {
        try
        {
            var filter = Builders<User>.Filter.Eq(u => u.Id, id);
            var result = _usersCollection.DeleteOne(filter);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    public bool DeactivateUser(string id)
    {
        try
        {
            var filter = Builders<User>.Filter.Eq(u => u.Id, id);
            var update = Builders<User>.Update.Set(u => u.Status, "terminated");
            var result = _usersCollection.UpdateOne(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            ExceptionService exceptionService = new();
            exceptionService.CreateLog(ex);
            throw new Exception(ex.Message);
        }
    }

    private void EnsureUniqueEmail()
    {
        var indexKeysDefinition = Builders<User>.IndexKeys.Ascending(u => u.Email);
        var indexOptions = new CreateIndexOptions { Unique = true };
        var indexModel = new CreateIndexModel<User>(indexKeysDefinition, indexOptions);
        _usersCollection.Indexes.CreateOneAsync(indexModel);
    }
}