using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Ruri.RipperHook.Core.TypeTree;

public static class TypeTreeFieldAccess
{
    private const BindingFlags DeclaredInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static FieldInfo? FindField(Type type, string name)
    {
        for (Type? current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, DeclaredInstance);
            if (field is not null)
            {
                return field;
            }
        }
        return null;
    }

    public static Action<object, T> CreateSetter<T>(FieldInfo field)
    {
        if (field.FieldType != typeof(T))
        {
            throw new ArgumentException($"[TypeTree] {Describe(field)} is not a {typeof(T).Name}.", nameof(field));
        }

        Type declaringType = field.DeclaringType!;
        DynamicMethod method = new(
            $"set_{declaringType.Name}_{field.Name}",
            typeof(void),
            [typeof(object), typeof(T)],
            declaringType.Module,
            skipVisibility: true);

        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, declaringType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);

        return method.CreateDelegate<Action<object, T>>();
    }

    public static Func<object, object?> CreateReferenceGetter(FieldInfo field)
    {
        if (field.FieldType.IsValueType)
        {
            throw new ArgumentException($"[TypeTree] {Describe(field)} is a value type; it must be assigned, not filled in place.", nameof(field));
        }

        Type declaringType = field.DeclaringType!;
        DynamicMethod method = new(
            $"get_{declaringType.Name}_{field.Name}",
            typeof(object),
            [typeof(object)],
            declaringType.Module,
            skipVisibility: true);

        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, declaringType);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);

        return method.CreateDelegate<Func<object, object?>>();
    }

    private static string Describe(FieldInfo field) => $"{field.DeclaringType?.Name}.{field.Name} ({field.FieldType.Name})";
}
