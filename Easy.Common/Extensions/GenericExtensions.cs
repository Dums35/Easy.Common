﻿// ReSharper disable AssignNullToNotNullAttribute
namespace Easy.Common.Extensions
{
    using System;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Runtime.Serialization;

    /// <summary>
    /// A set of extension methods for generic types.
    /// </summary>
    public static class GenericExtensions
    {
        /// <summary>
        /// This dictionary caches the delegates for each 'to-clone' type.
        /// </summary>
        private static readonly Dictionary<Type, Delegate> CachedIlShallow = new Dictionary<Type, Delegate>();
        private static readonly Dictionary<Type, Delegate> CachedIlDeep = new Dictionary<Type, Delegate>();
        private static LocalBuilder _localBuilder;

        /// <summary>
        /// Converts the given <paramref name="object"/> to a <see cref="DynamicDictionary"/>.
        /// </summary>
        public static DynamicDictionary ToDynamic<T>(this T @object, bool inherit = true)
        {
            var dynDic = new DynamicDictionary();

            foreach (var property in @object.GetType().GetInstanceProperties(inherit))
            {
                dynDic.Add(property.Name, property.GetValue(@object, null));
            }

            return dynDic;
        }

        /// <summary>
        /// Returns <c>True</c> if <paramref name="object"/> has the default value of <typeparamref name="T"/>.
        /// </summary>
        /// <param name="object">The object to check for default.</param>
        /// <returns><c>True</c> if <paramref name="object"/> has default or null value otherwise <c>False</c>.</returns>
        public static bool IsDefault<T>(this T @object)
        {
            return EqualityComparer<T>.Default.Equals(@object, default(T));
        }

        /// <summary>
        /// Returns an uninitialized instance of the <typeparamref name="T"/> without calling any of its constructor(s).
        /// <see href="https://msdn.microsoft.com/en-us/library/system.runtime.serialization.formatterservices.getuninitializedobject.aspx"/>
        /// </summary>
        /// <remarks>
        /// Because the new instance of the object is initialized to zero and no constructors are run, 
        /// the object might not represent a state that is regarded as valid by that object. 
        /// The current method should only be used for deserialization when the user intends to immediately
        /// populate all fields. It does not create an uninitialized string, 
        /// since creating an empty instance of an immutable type serves no purpose.
        /// </remarks>
        /// <typeparam name="T">Generic Type</typeparam>
        /// <returns>An instance of type <typeparamref name="T"/> 
        /// with all its <c>non-static</c> fields initialized to its default value.
        /// </returns>
        public static T GetUninitializedInstance<T>()
        {
            return (T)FormatterServices.GetUninitializedObject(typeof(T));
        }

        /// <summary>
        /// Gets all the private, public, inherited instance property names for the given <paramref name="@object"/>.
        /// <remarks>This method can be used to return both a <c>public</c> or <c>non-public</c> property names.</remarks>
        /// </summary>
        public static string[] GetPropertyNames<T>(this T @object, bool inherit = true, bool includePrivate = true)
        {
            var expando = @object as IDynamicMetaObjectProvider;
            if (expando != null)
            {
                var dic = (IDictionary<string, object>)expando;
                return dic.Keys.ToArray();
            }

            return @object.GetType()
                .GetInstanceProperties(inherit, includePrivate)
                .Select(p => p.Name).ToArray();
        }

        /// <summary>    
        /// Generic cloning method that clones an object using IL.    
        /// Only the first call of a certain type will hold back performance.    
        /// After the first call, the compiled IL is executed.    
        /// </summary>    
        /// <typeparam name="T">Type of object to clone</typeparam>    
        /// <param name="myObject">Object to clone</param>    
        /// <returns>Cloned object</returns>    
        public static T CloneShallowUsingIl<T>(this T myObject)
        {
            Delegate myExec;
            if (!CachedIlShallow.TryGetValue(typeof(T), out myExec))
            {
                // Create ILGenerator (both DM declarations work)
                var dymMethod = new DynamicMethod("DoClone", typeof(T), new[] { typeof(T) }, Assembly.GetExecutingAssembly().ManifestModule, true);
                var cInfo = myObject.GetType().GetConstructor(new Type[] { });
                var generator = dymMethod.GetILGenerator();

                generator.Emit(OpCodes.Newobj, cInfo);
                generator.Emit(OpCodes.Stloc_0);
                foreach (FieldInfo field in myObject.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    generator.Emit(OpCodes.Ldloc_0);
                    generator.Emit(OpCodes.Ldarg_0);
                    generator.Emit(OpCodes.Ldfld, field);
                    generator.Emit(OpCodes.Stfld, field);
                }
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Ret);
                myExec = dymMethod.CreateDelegate(typeof(Func<T, T>));
                CachedIlShallow.Add(typeof(T), myExec);
            }
            return ((Func<T, T>)myExec)(myObject);
        }

        /// <summary>
        /// Performs a deep copy of the object using the by generating IL.
        /// Only the first call for a certain type will have impact on performance; After the first call, the compiled IL is executed.
        /// <see href="http://whizzodev.blogspot.co.uk/2008/06/object-deep-cloning-using-il-in-c.html"/>
        /// </summary>    
        /// <typeparam name="T">The type of object being cloned.</typeparam>    
        /// <param name="myObject">The object instance to clone.</param>    
        /// <returns>the cloned object</returns>    
        public static T CloneDeepUsingIl<T>(this T myObject)
        {
            Delegate myExec;
            if (!CachedIlDeep.TryGetValue(typeof(T), out myExec))
            {
                // Create ILGenerator (both DM declarations work)
                var dymMethod = new DynamicMethod("DoClone", typeof(T), new[] { typeof(T) }, Assembly.GetExecutingAssembly().ManifestModule, true);
                var cInfo = myObject.GetType().GetConstructor(new Type[] { });
                var generator = dymMethod.GetILGenerator();

                generator.Emit(OpCodes.Newobj, cInfo);
                generator.Emit(OpCodes.Stloc_0);

                foreach (var field in typeof(T).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (field.FieldType.IsValueType || field.FieldType == typeof(string))
                    {
                        CopyValueType(generator, field);
                    }
                    else if (field.FieldType.IsClass)
                    {
                        CopyReferenceType(generator, field);
                    }
                }
                generator.Emit(OpCodes.Ldloc_0);
                generator.Emit(OpCodes.Ret);
                myExec = dymMethod.CreateDelegate(typeof(Func<T, T>));
                CachedIlDeep.Add(typeof(T), myExec);
            }
            return ((Func<T, T>)myExec)(myObject);
        }

        private static void CopyValueType(ILGenerator generator, FieldInfo field)
        {
            generator.Emit(OpCodes.Ldloc_0);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, field);
            generator.Emit(OpCodes.Stfld, field);
        }

        private static void CopyReferenceType(ILGenerator generator, FieldInfo field)
        {
            // We have a reference type.
            _localBuilder = generator.DeclareLocal(field.FieldType);
            if (field.FieldType.GetInterface("IEnumerable") != null)
            {
                // We have a list type (generic).
                if (field.FieldType.IsGenericType)
                {
                    // Get argument of list type
                    var argType = field.FieldType.GetGenericArguments()[0];
                    // Check that it has a constructor that accepts another IEnumerable.
                    var genericType = typeof(IEnumerable<>).MakeGenericType(argType);

                    var ci = field.FieldType.GetConstructor(new[] { genericType });
                    if (ci != null)
                    {
                        // It has! (Like the List<> class)
                        generator.Emit(OpCodes.Ldarg_0);
                        generator.Emit(OpCodes.Ldfld, field);
                        generator.Emit(OpCodes.Newobj, ci);
                        generator.Emit(OpCodes.Stloc, _localBuilder);
                        PlaceNewTempObjInClone(generator, field);
                    }
                }
            }
            else
            {
                CreateNewTempObject(generator, field.FieldType);
                PlaceNewTempObjInClone(generator, field);
                foreach (var fi in field.FieldType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (fi.FieldType.IsValueType || fi.FieldType == typeof(string))
                    {
                        CopyValueTypeTemp(generator, field, fi);
                    }
                    else if (fi.FieldType.IsClass)
                    {
                        CopyReferenceType(generator, fi);
                    }
                }
            }
        }

        private static void CreateNewTempObject(ILGenerator generator, Type type)
        {
            var cInfo = type.GetConstructor(new Type[] { });
            generator.Emit(OpCodes.Newobj, cInfo);
            generator.Emit(OpCodes.Stloc, _localBuilder);
        }

        private static void PlaceNewTempObjInClone(ILGenerator generator, FieldInfo field)
        {
            // Get object from custom location and store it in right field of location 0
            generator.Emit(OpCodes.Ldloc_0);
            generator.Emit(OpCodes.Ldloc, _localBuilder);
            generator.Emit(OpCodes.Stfld, field);
        }

        private static void CopyValueTypeTemp(ILGenerator generator, FieldInfo fieldParent, FieldInfo fieldDetail)
        {
            generator.Emit(OpCodes.Ldloc_1);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldfld, fieldParent);
            generator.Emit(OpCodes.Ldfld, fieldDetail);
            generator.Emit(OpCodes.Stfld, fieldDetail);
        }
    }

}