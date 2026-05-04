using System;

namespace Ruri.Hook.Attributes
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class RetargetMethodCtorFuncAttribute : Attribute
    {
        public RetargetMethodCtorFuncAttribute(Type sourceType, params Type[]? methodParameters)
        {
            SourceType = sourceType;
            MethodParameters = methodParameters;
        }

        public Type[]? MethodParameters { get; }
        public Type SourceType { get; }
    }
}
