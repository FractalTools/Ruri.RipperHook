using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// Binds a type tree node onto the backing field the assembly dumper generated for it, and builds
/// unboxed accessors for it.
///
/// The generated AssetRipper classes name every serialized field exactly after its (sanitized) node
/// -- <c>m_SubMeshes</c>, <c>m_MeshMetrics_0_</c>, <c>m_Name</c> -- with <c>assembly</c> visibility,
/// and inherited fields stay declared on the base class, so the lookup walks the base chain.
/// Accessors are emitted rather than reflected because a <see cref="FieldInfo.SetValue(object, object)"/>
/// per primitive field would box on every asset.
/// </summary>
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

    /// <summary>Emits <c>((TDeclaring)target).field = value</c>.</summary>
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

    /// <summary>
    /// Emits <c>((TDeclaring)target).field</c> for a reference-typed field. Used for the fields the
    /// read path fills in place (subclass instances, <c>AssetList</c>, <c>AssetDictionary</c>), which
    /// the generated constructors already allocated.
    /// </summary>
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
