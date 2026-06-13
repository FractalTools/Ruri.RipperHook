using System.Linq.Expressions;
using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.UnityExport.Engine;

// Fluent, statically-typed mapping builder for one (TSrc UObject -> TDst Unity
// object) pair. Set() records a target property (extracted once from an
// expression tree) plus a plain source lambda; Apply() constructs the Unity
// object and runs every setter.
//
// Field assignment is a single reflection SetValue per property — deliberately
// NOT Expression.Compile'd: the per-asset cost (a few dozen SetValues) is
// negligible next to package I/O and decode, and reflection-only keeps the
// engine simple and debuggable (FModelHook design note). The SOURCE side stays
// a normal lambda, so it is fully type-checked at compile time and any
// CUE4Parse-side rename breaks the build immediately.
public sealed class Mapping<TSrc, TDst> : IUnityObjectMapping
    where TSrc : UObject
    where TDst : IUnityObjectBase
{
    private readonly Func<ProcessedAssetCollection, TDst> _create;
    private readonly List<FieldSetter> _setters = new();

    internal Mapping(Func<ProcessedAssetCollection, TDst> create) => _create = create;

    public Type SourceType => typeof(TSrc);

    // Bind one target property to a source expression. `target` must be a simple
    // property access (optionally wrapped in the value-type Convert node the
    // compiler inserts when widening TVal); anything else is a registration-time
    // usage error, never a per-asset surprise.
    public Mapping<TSrc, TDst> Set<TVal>(Expression<Func<TDst, TVal>> target, Func<TSrc, TVal> source)
    {
        MemberExpression member = target.Body as MemberExpression
            ?? (target.Body as UnaryExpression)?.Operand as MemberExpression
            ?? throw new ArgumentException($"Set target must be a property access, got: {target.Body}");
        PropertyInfo property = member.Member as PropertyInfo
            ?? throw new ArgumentException($"Set target must be a property, got member: {member.Member.Name}");

        _setters.Add(new FieldSetter(property.Name, (src, dst) => property.SetValue(dst, source(src))));
        return this;
    }

    public IUnityObjectBase Apply(UObject source, ProcessedAssetCollection collection)
    {
        TDst destination = _create(collection);
        TSrc typedSource = (TSrc)source;
        foreach (FieldSetter setter in _setters)
        {
            try
            {
                setter.Apply(typedSource, destination);
            }
            catch (Exception ex)
            {
                // Surface WHICH field blew up; MapperRegistry.Convert adds the asset
                // identity. Without this, one bad field in a thousand-asset run is
                // impossible to locate.
                throw new InvalidOperationException($"setter '{setter.FieldName}' failed: {ex.Message}", ex);
            }
        }
        return destination;
    }

    private readonly struct FieldSetter
    {
        public readonly string FieldName;
        private readonly Action<TSrc, TDst> _apply;

        public FieldSetter(string fieldName, Action<TSrc, TDst> apply)
        {
            FieldName = fieldName;
            _apply = apply;
        }

        public void Apply(TSrc source, TDst destination) => _apply(source, destination);
    }
}
