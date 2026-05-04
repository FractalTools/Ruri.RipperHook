using System;

namespace Ruri.Hook.Attributes
{
    /// <summary>
    /// 除非迫不得已 严禁使用此特性 因为代码过于依赖二进制 可维护性极差
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class RetargetMethodFuncAttribute : Attribute
    {
        public RetargetMethodFuncAttribute(Type sourceType)
        {
            SourceType = sourceType;
            SourceMethodName = null;
        }

        public RetargetMethodFuncAttribute(Type sourceType, string sourceMethodName, params Type[]? methodParameters)
        {
            SourceType = sourceType;
            SourceMethodName = sourceMethodName;
            MethodParameters = methodParameters;
        }
        
        public RetargetMethodFuncAttribute(string sourceTypeName, string sourceMethodName, params Type[]? methodParameters)
        {
            SourceType = Type.GetType(sourceTypeName) ?? throw new Exception($"Type {sourceTypeName} not found");
            SourceMethodName = sourceMethodName;
            MethodParameters = methodParameters;
        }

        public Type[]? MethodParameters { get; }
        public Type SourceType { get; }
        public string SourceMethodName { get; }
    }
}
