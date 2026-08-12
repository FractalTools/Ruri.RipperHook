using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MonoMod.Cil;
using Ruri.Hook.Attributes;
using Ruri.Hook.Utils;

namespace Ruri.Hook.Core
{
    public class HookRegistry
    {
        public void ApplyAttributeHooks(Assembly assembly, string? targetGameName = null, IEnumerable<MethodInfo>? manualMethods = null)
        {
            var bindingFlags = ReflectionExtensions.AnyBindFlag();
            var types = assembly.GetTypes();

            IEnumerable<Type> targetTypes;

            if (!string.IsNullOrEmpty(targetGameName))
            {
                targetTypes = types.Where(t => 
                {
                    var attr = t.GetCustomAttribute<GameHookAttribute>();
                    return attr != null && attr.GameName == targetGameName;
                });
            }
            else
            {
                targetTypes = Enumerable.Empty<Type>();
            }

            var scannedMethods = targetTypes.SelectMany(t => t.GetMethods(bindingFlags));

            var allMethods = manualMethods != null 
                ? scannedMethods.Concat(manualMethods).Distinct() 
                : scannedMethods;

            ApplyRetargetMethodAttributes(allMethods);
            ApplyRetargetMethodFuncAttributes(allMethods);
            ApplyRetargetMethodCtorFuncAttributes(allMethods);
        }

        public void ApplyTypeHooks(Type type)
        {
            var bindingFlags = ReflectionExtensions.AnyBindFlag();
            var methods = type.GetMethods(bindingFlags);
            
            ApplyRetargetMethodAttributes(methods);
            ApplyRetargetMethodFuncAttributes(methods);
            ApplyRetargetMethodCtorFuncAttributes(methods);
        }

        public void ApplyManualHooks(IEnumerable<MethodInfo> methods)
        {
            ApplyRetargetMethodAttributes(methods);
            ApplyRetargetMethodFuncAttributes(methods);
            ApplyRetargetMethodCtorFuncAttributes(methods);
        }

        private void ApplyRetargetMethodAttributes(IEnumerable<MethodInfo> methods)
        {
             var targetMethods = methods.Where(m => m.GetCustomAttributes<RetargetMethodAttribute>(true).Any());

             foreach (var methodDest in targetMethods)
             {
                 var attrs = methodDest.GetCustomAttributes<RetargetMethodAttribute>();
                 foreach (var attr in attrs)
                 {
                     ProcessRetarget(methodDest, attr);
                 }
             }
        }

        private void ProcessRetarget(MethodInfo methodDest, RetargetMethodAttribute attr)
        {
            var bindingFlags = ReflectionExtensions.AnyBindFlag();
            MethodInfo? methodSrc;
            
            var methodName = attr.SourceMethodName;
            if (string.IsNullOrEmpty(methodName))
            {
                methodName = methodDest.Name;
                var prefix = (attr.SourceType?.Name ?? attr.SourceTypeName?.Split('.').Last()) + "_";
                if (methodName.StartsWith(prefix))
                {
                    methodName = methodName.Substring(prefix.Length);
                }
            }

            Type? sourceType = attr.SourceType;
            if (sourceType == null && !string.IsNullOrEmpty(attr.SourceTypeName))
            {
               sourceType = Type.GetType(attr.SourceTypeName);
               
               if (sourceType == null)
               {
                   foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                   {
                       sourceType = asm.GetType(attr.SourceTypeName);
                       if (sourceType != null) break;
                   }
               }
            }

            if (sourceType == null)
            {
                throw new Exception($"[HookRegistry] Could not resolve source type '{attr.SourceTypeName ?? "null"}'");
            }

            if (attr.MethodParameters == null)
            {
                methodSrc = sourceType.GetMethod(methodName, bindingFlags);
            }
            else
            {
                methodSrc = sourceType.GetMethod(methodName, bindingFlags, attr.MethodParameters);

                if (methodSrc == null && attr.MethodParameters.Length == 0)
                {
                    try 
                    {
                        methodSrc = sourceType.GetMethod(methodName, bindingFlags);
                    }
                    catch (AmbiguousMatchException) { }

                    if (methodSrc == null)
                    {
                         methodSrc = sourceType.GetMethods(bindingFlags).FirstOrDefault(m => m.Name == methodName);
                    }
                }
            }

            if (methodSrc == null && string.IsNullOrEmpty(attr.SourceMethodName))
            {
                 if (methodName.Contains("_"))
                 {
                     var relaxedName = methodName.Substring(methodName.IndexOf('_') + 1);
                     try 
                     {
                         if (attr.MethodParameters == null)
                         {
                             methodSrc = sourceType.GetMethod(relaxedName, bindingFlags);
                         }
                         else
                         {
                             methodSrc = sourceType.GetMethod(relaxedName, bindingFlags, attr.MethodParameters);
                         }

                         if (methodSrc == null && (attr.MethodParameters == null || attr.MethodParameters.Length == 0))
                         {
                              methodSrc = sourceType.GetMethods(bindingFlags).FirstOrDefault(m => m.Name == relaxedName);
                         }
                     }
                     catch (AmbiguousMatchException) { }
                 }
            }

            if (methodSrc == null)
                 throw new Exception($"[HookRegistry] Could not find source method {sourceType.Name}.{methodName} (Relaxed lookup also failed)");

            int srcParamCount = methodSrc.GetParameters().Length;

            if (methodSrc.IsStatic) srcParamCount--;

            try
            {
                ReflectionExtensions.RetargetCall(methodSrc, methodDest, srcParamCount, attr.IsBefore, attr.IsReturn);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"[HookRegistry] Retarget failed: {methodSrc.DeclaringType?.FullName}.{methodSrc.Name} -> " +
                    $"{methodDest.DeclaringType?.FullName}.{methodDest.Name}", ex);
            }
        }

        private void ApplyRetargetMethodFuncAttributes(IEnumerable<MethodInfo> methods)
        {
             var targetMethods = methods.Where(m => m.GetCustomAttributes<RetargetMethodFuncAttribute>(true).Any());
             foreach(var methodDest in targetMethods)
             {
                 foreach(var attr in methodDest.GetCustomAttributes<RetargetMethodFuncAttribute>())
                 {
                     var bindingFlags = ReflectionExtensions.AnyBindFlag();
                     
                     var methodName = attr.SourceMethodName;
                     if (string.IsNullOrEmpty(methodName))
                     {
                        methodName = methodDest.Name;
                        var prefix = attr.SourceType.Name + "_";
                        if (methodName.StartsWith(prefix))
                        {
                            methodName = methodName.Substring(prefix.Length);
                        }
                     }

                     MethodInfo? methodSrc = null;

                     if (attr.MethodParameters != null)
                     {
                        methodSrc = attr.SourceType.GetMethod(methodName, bindingFlags, attr.MethodParameters);
                     }
                     if (methodSrc == null)
                     {
                         try 
                         {
                            methodSrc = attr.SourceType.GetMethod(methodName, bindingFlags);
                         } 
                         catch (AmbiguousMatchException) { }
                     }

                     if (methodSrc == null)
                     {
                         methodSrc = attr.SourceType.GetMethods(bindingFlags).FirstOrDefault(m => m.Name == methodName);
                     }

                     if (methodSrc == null)
                         throw new Exception($"[HookRegistry] Could not find source method {attr.SourceType.Name}.{methodName}");

                     var hookCallback = (Func<ILContext, bool>)Delegate.CreateDelegate(typeof(Func<ILContext, bool>), methodDest);
                     ReflectionExtensions.RetargetCallFunc(hookCallback, methodSrc);
                 }
             }
        }

        private void ApplyRetargetMethodCtorFuncAttributes(IEnumerable<MethodInfo> methods)
        {
             var targetMethods = methods.Where(m => m.GetCustomAttributes<RetargetMethodCtorFuncAttribute>(true).Any());
             foreach(var methodDest in targetMethods)
             {
                 foreach(var attr in methodDest.GetCustomAttributes<RetargetMethodCtorFuncAttribute>())
                 {
                     var bindingFlags = ReflectionExtensions.AnyBindFlag();
                     ConstructorInfo? methodSrc = attr.MethodParameters == null 
                        ? attr.SourceType.GetConstructor(Type.EmptyTypes)
                        : attr.SourceType.GetConstructor(bindingFlags, attr.MethodParameters);

                     if (methodSrc == null)
                         throw new Exception($"[HookRegistry] Could not find source constructor {attr.SourceType.Name}");

                     var hookCallback = (Func<ILContext, bool>)Delegate.CreateDelegate(typeof(Func<ILContext, bool>), methodDest);
                     ReflectionExtensions.RetargetCallCtorFunc(hookCallback, methodSrc);
                 }
             }
        }
    }
}
