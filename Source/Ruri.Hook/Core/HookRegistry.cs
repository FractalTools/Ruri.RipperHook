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
        /// <summary>
        /// Scans the assembly for methods with RetargetMethod attributes.
        /// Supports filtering by GameName if provided.
        /// Also incorporates manually added method hooks for absolute compatibility.
        /// </summary>
        public void ApplyAttributeHooks(Assembly assembly, string? targetGameName = null, IEnumerable<MethodInfo>? manualMethods = null)
        {
            var bindingFlags = ReflectionExtensions.AnyBindFlag();
            var types = assembly.GetTypes();

            IEnumerable<Type> targetTypes;

            if (!string.IsNullOrEmpty(targetGameName))
            {
                // Attribute based filtering: Only scan types with [GameHook(targetGameName)]
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
                // Infer method name from hook method name
                // Pattern: [SourceType]_[MethodName] or just [MethodName]
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
               // Try Type.GetType first
               sourceType = Type.GetType(attr.SourceTypeName);
               
               // Fallback: Scan all loaded assemblies (Robust to cross-assembly references)
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

                // Fallback: If parameters were empty (default for params) and we didn't find a match, 
                // try looking up by name only using LINQ (Robust fallback).
                if (methodSrc == null && attr.MethodParameters.Length == 0)
                {
                    try 
                    {
                        methodSrc = sourceType.GetMethod(methodName, bindingFlags);
                    }
                    catch (AmbiguousMatchException) { }

                    if (methodSrc == null)
                    {
                         // Final fallback: LINQ search (ignores overloads, takes first)
                         methodSrc = sourceType.GetMethods(bindingFlags).FirstOrDefault(m => m.Name == methodName);
                    }
                }
            }

            // Extended Fallback: If method is still not found, and we inferred the name, try relaxed naming conventions
            if (methodSrc == null && string.IsNullOrEmpty(attr.SourceMethodName))
            {
                 // Logic for Endfield Hook inequivalence: Mesh_ReadRelease -> ReadRelease (Strip first segment)
                 // This handles cases where the hook method includes a prefix that doesn't match the ClassName
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

                         // LINQ fallback for relaxed name (if parameters match failed or were empty)
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
            // int destParamCount = methodDest.GetParameters().Length; // Unused?

            if (methodSrc.IsStatic) srcParamCount--;
            
            ReflectionExtensions.RetargetCall(methodSrc, methodDest, srcParamCount, attr.IsBefore, attr.IsReturn);
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

                     // 1. Try exact parameter match (if params provided)
                     if (attr.MethodParameters != null)
                     {
                        methodSrc = attr.SourceType.GetMethod(methodName, bindingFlags, attr.MethodParameters);
                     }
                     // 2. Try exact name match (without params or if attr.Parameters was null)
                     if (methodSrc == null)
                     {
                         try 
                         {
                            methodSrc = attr.SourceType.GetMethod(methodName, bindingFlags);
                         } 
                         catch (AmbiguousMatchException) { }
                     }

                     // 3. Fallback: LINQ search by name (first match)
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
