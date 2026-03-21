using System.Reflection;

namespace Ruri.RipperHook.Endfield;

public partial class EndField_0_8_25_Hook
{
    public static void SetPrivateProperty(object instance, string propertyName, object value)
    {
        var type = instance.GetType();
        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop == null)
            throw new Exception($"Property {propertyName} not found on {type.FullName}");

        var setter = prop.GetSetMethod(true);
        if (setter == null)
            throw new Exception($"Property {propertyName} has no setter on {type.FullName}");

        setter.Invoke(instance, new object[] { value });
    }
}