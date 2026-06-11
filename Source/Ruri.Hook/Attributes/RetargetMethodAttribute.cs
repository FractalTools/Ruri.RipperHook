using System;

namespace Ruri.Hook.Attributes
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class RetargetMethodAttribute : Attribute
    {
        public RetargetMethodAttribute(Type sourceType)
        {
            SourceType = sourceType;
            SourceMethodName = null;
        }

        public RetargetMethodAttribute(string sourceTypeName, string sourceMethodName)
        {
            SourceTypeName = sourceTypeName;
            SourceMethodName = sourceMethodName;
        }

        // String-typed target with explicit injection mode. Use when the source type lives in
        // another assembly and is internal (can't be referenced via typeof) — the registry resolves
        // it by scanning loaded assemblies. isBefore/isReturn pick prefix-replace / prefix-continue /
        // postfix as documented on the Type-typed overload.
        public RetargetMethodAttribute(string sourceTypeName, string sourceMethodName, bool isBefore, bool isReturn, params Type[]? methodParameters)
        {
            SourceTypeName = sourceTypeName;
            SourceMethodName = sourceMethodName;
            IsBefore = isBefore;
            IsReturn = isReturn;
            MethodParameters = methodParameters;
        }

        public RetargetMethodAttribute(Type sourceType, string sourceMethodName, params Type[]? methodParameters)
        {
            SourceType = sourceType;
            SourceMethodName = sourceMethodName;
            MethodParameters = methodParameters;
        }

        public RetargetMethodAttribute(Type sourceType, string sourceMethodName, bool isBefore = true, bool isReturn = true, params Type[]? methodParameters) 
            : this(sourceType, sourceMethodName, methodParameters)
        {
            IsBefore = isBefore;
            IsReturn = isReturn;
        }

        public Type[]? MethodParameters { get; }
        public Type? SourceType { get; }
        public string? SourceTypeName { get; }
        public string SourceMethodName { get; }
        
        // Defaults to true (Prefix) and true (Return immediate)
        public bool IsBefore { get; } = true;
        public bool IsReturn { get; } = true;
    }
}
