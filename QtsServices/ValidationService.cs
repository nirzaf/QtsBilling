using System.Reflection;

namespace QtsServices;

public static class ValidationService
{
    //generate a method to validate if an object is Empty or null or not
    public static bool IsEmpty<T>(this object? obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        // Get all fields of the object
        FieldInfo[] fields = typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var field in fields)
        {
            // Get the value of the field
            object? value = field.GetValue(obj);

            // Check if the field is not empty (null, 0, or default)
            if (value != null && !value.Equals(GetDefaultValue(field.FieldType)))
            {
                return false; // At least one field is not empty
            }
        }

        return true; // All fields are empty
    }

    private static object? GetDefaultValue(Type type)
    {
        // Get the default value for the field type
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

}