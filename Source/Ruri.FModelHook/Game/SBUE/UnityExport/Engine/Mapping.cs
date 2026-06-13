using System.Linq.Expressions;
using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

// Fluent, statically-typed mapping builder for one (TSrc UObject -> TDst Unity
// object) pair. Set() records a target property (extracted once from an
// expression tree) plus a source lambda; After() records a post-construct
// customization for cases a single property assignment can't express (filling a
// sub-object's dictionary, mutating texture settings, ...).
//
// Field assignment is a single reflection SetValue per property — deliberately
// NOT Expression.Compile'd: the per-asset cost is negligible next to package I/O
// and decode, and reflection-only keeps the engine simple and debuggable
// (FModelHook design note). The SOURCE side stays a normal lambda, fully
// type-checked at compile time, so any CUE4Parse-side rename breaks the build.
public sealed class Mapping<TSrc, TDst> : IUnityObjectMapping
    where TSrc : UObject
    where TDst : IUnityObjectBase
{
    private readonly Func<ConversionContext, TDst> _create;
    private readonly List<FieldSetter> _setters = new();

    internal Mapping(Func<ProcessedAssetCollection, TDst> create)
        => _create = context => create(context.Collection);

    public Type SourceType => typeof(TSrc);

    // Bind one target property to a plain source expression (the common case:
    // self-contained field of the source object).
    public Mapping<TSrc, TDst> Set<TVal>(Expression<Func<TDst, TVal>> target, Func<TSrc, TVal> source)
        => Set(target, (s, _) => source(s));

    // Bind one target property to a context-aware source — for fields that need
    // to resolve cross-references (PPtrs to other converted assets).
    public Mapping<TSrc, TDst> Set<TVal>(Expression<Func<TDst, TVal>> target, Func<TSrc, ConversionContext, TVal> source)
    {
        PropertyInfo property = ExtractProperty(target);
        _setters.Add(new FieldSetter(property.Name, (s, d, ctx) => property.SetValue(d, source(s, ctx))));
        return this;
    }

    // Post-construct customization: sub-object population (m_TexEnvs, blend-shape
    // channels), nested settings, anything a single SetValue can't express.
    // Runs in declaration order alongside the setters.
    public Mapping<TSrc, TDst> After(Action<TSrc, TDst, ConversionContext> action)
    {
        _setters.Add(new FieldSetter("<after>", action));
        return this;
    }

    public IUnityObjectBase Create(ConversionContext context) => _create(context);

    public void Populate(UObject source, IUnityObjectBase destination, ConversionContext context)
    {
        TSrc typedSource = (TSrc)source;
        TDst typedDestination = (TDst)destination;
        foreach (FieldSetter setter in _setters)
        {
            try
            {
                setter.Apply(typedSource, typedDestination, context);
            }
            catch (Exception ex)
            {
                // Surface WHICH field blew up; ConversionContext.Convert adds the
                // asset identity. Without this, one bad field in a thousand-asset
                // run is impossible to locate.
                throw new InvalidOperationException($"setter '{setter.FieldName}' failed: {ex.Message}", ex);
            }
        }
    }

    // `target` must be a simple property access (optionally wrapped in the
    // value-type Convert node the compiler inserts when widening TVal); anything
    // else is a registration-time usage error, never a per-asset surprise.
    private static PropertyInfo ExtractProperty<TVal>(Expression<Func<TDst, TVal>> target)
    {
        MemberExpression member = target.Body as MemberExpression
            ?? (target.Body as UnaryExpression)?.Operand as MemberExpression
            ?? throw new ArgumentException($"Set target must be a property access, got: {target.Body}");
        return member.Member as PropertyInfo
            ?? throw new ArgumentException($"Set target must be a property, got member: {member.Member.Name}");
    }

    private readonly struct FieldSetter
    {
        public readonly string FieldName;
        private readonly Action<TSrc, TDst, ConversionContext> _apply;

        public FieldSetter(string fieldName, Action<TSrc, TDst, ConversionContext> apply)
        {
            FieldName = fieldName;
            _apply = apply;
        }

        public void Apply(TSrc source, TDst destination, ConversionContext context)
            => _apply(source, destination, context);
    }
}
