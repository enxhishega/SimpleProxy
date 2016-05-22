﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Kontur.Elba.Core.Utilities.Reflection
{
	public static class SimpleProxyFactory
	{
		private static readonly ConcurrentDictionary<Type, Type> types = new ConcurrentDictionary<Type, Type>();

		private static readonly ConcurrentDictionary<MethodInfo, Func<object, object[], object>> methods
			= new ConcurrentDictionary<MethodInfo, Func<object, object[], object>>();

		public static TInterface CreateProxyWithoutTarget<TInterface>(IHandler handler)
		{
			return CreateProxyForTarget(new Adapter(handler), default(TInterface));
		}

		public static TInterface CreateProxyForTarget<TInterface>(IInterceptor interceptor, TInterface target)
		{
			var typeBuilder = types.GetOrAdd(typeof(TInterface), _ => EmitProxyForTarget<TInterface>());
			return (TInterface)Activator.CreateInstance(typeBuilder, interceptor, target);
		}

		private static Type EmitProxyForTarget<TInterface>()
		{
			if (!typeof(TInterface).IsInterface)
				throw new InvalidOperationException(string.Format("{0} is not an interface type", typeof(TInterface)));
			var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(typeof(SimpleProxyFactory).Name), AssemblyBuilderAccess.RunAndCollect);
			var typeBuilder = assemblyBuilder.DefineDynamicModule("main").DefineType(typeof(TInterface) + "_proxy", TypeAttributes.Public);
			typeBuilder.AddInterfaceImplementation(typeof(TInterface));
			var interceptorField = typeBuilder.DefineField("interceptor", typeof(IInterceptor), FieldAttributes.Private);
			var proxyField = typeBuilder.DefineField("proxy", typeof(TInterface), FieldAttributes.Private);
			var ctor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis,
													 new[] { typeof(IInterceptor), typeof(TInterface) });
			var ctorIl = ctor.GetILGenerator();
			ctorIl.Emit(OpCodes.Ldarg_0);
			ctorIl.Emit(OpCodes.Ldarg_1);
			ctorIl.Emit(OpCodes.Stfld, interceptorField);
			ctorIl.Emit(OpCodes.Ldarg_0);
			ctorIl.Emit(OpCodes.Ldarg_2);
			ctorIl.Emit(OpCodes.Stfld, proxyField);
			ctorIl.Emit(OpCodes.Ret);
			foreach (var originalMethod in typeof(TInterface).GetMethods(BindingFlags.Instance | BindingFlags.Public))
			{
				var proxyImplMethod = typeBuilder.DefineMethod(originalMethod.Name,
															   originalMethod.Attributes & ~MethodAttributes.Abstract,
															   originalMethod.CallingConvention, originalMethod.ReturnType,
															   originalMethod.GetParameters().Select(c => c.ParameterType).ToArray());
				if (originalMethod.IsGenericMethod)
				{
					var originalGenericArgs = originalMethod.GetGenericArguments();
					var genericParams = proxyImplMethod.DefineGenericParameters(originalGenericArgs.Select(x => "T" + x.Name).ToArray());
					foreach (var tuple in genericParams.Zip(originalGenericArgs, Tuple.Create))
					{
						var interfaceConstraints = tuple.Item2.GetGenericParameterConstraints().Where(x => x.IsInterface).ToArray();
						var baseTypeConstraint = tuple.Item2.GetGenericParameterConstraints().FirstOrDefault(x => x.IsClass);
						if (baseTypeConstraint != null)
							tuple.Item1.SetBaseTypeConstraint(baseTypeConstraint);
						tuple.Item1.SetInterfaceConstraints(interfaceConstraints);
					}
				}
				var ilg = proxyImplMethod.GetILGenerator();
				// load interceptor
				ilg.Emit(OpCodes.Ldarg_0);
				ilg.Emit(OpCodes.Ldfld, interceptorField);
				//new interceptor args
				ilg.Emit(OpCodes.Ldarg_0);
				ilg.Emit(OpCodes.Ldfld, proxyField);
				EmitLoadMethodInfo(ilg, originalMethod, typeof(TInterface));
				ilg.Emit(OpCodes.Call, typeof(SimpleProxyFactory).GetMethods(BindingFlags.Static | BindingFlags.Public).Single(x => x.Name == "EmitCallOf"));

				EmitNewMethodInvocation(ilg, originalMethod, typeof(TInterface));
				var interceptorArgsCtor = typeof(InterceptorArgs).GetConstructor(new[] { typeof(object), typeof(Func<object, object[], object>), typeof(MethodInvocation) });
				ilg.Emit(OpCodes.Newobj, interceptorArgsCtor);
				ilg.DeclareLocal(typeof(InterceptorArgs));
				ilg.Emit(OpCodes.Stloc_0);
				ilg.Emit(OpCodes.Ldloc_0);
				// call interceptor handle method
				var interceptorHandleMethod = typeof(IInterceptor).GetMethod("Handle");
				ilg.Emit(OpCodes.Callvirt, interceptorHandleMethod);
				//get return value
				if (originalMethod.ReturnType != typeof(void))
				{
					ilg.Emit(OpCodes.Ldloc_0);
					ilg.Emit(OpCodes.Ldfld, typeof(InterceptorArgs).GetField("Result"));
					ilg.Emit(originalMethod.ReturnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, originalMethod.ReturnType);
				}
				ilg.Emit(OpCodes.Ret);
			}
			return typeBuilder.CreateType();
		}

		// ReSharper disable once UnusedMember.Local
		public static Func<object, object[], object> EmitCallOf(MethodInfo method)
		{
			return methods.GetOrAdd(method, DoEmitCallOf);
		}

		public static Func<object, object[], object> DoEmitCallOf(MethodInfo targetMethod)
		{
			var dynamicMethod = new DynamicMethod("callOf_" + targetMethod.Name,
				typeof (object),
				new[] {typeof (object), typeof (object[])},
				typeof (SimpleProxyFactory),
				true);
			var il = dynamicMethod.GetILGenerator();
			if (!targetMethod.IsStatic)
			{
				il.Emit(OpCodes.Ldarg_0);
				if (targetMethod.DeclaringType.IsValueType)
				{
					il.Emit(OpCodes.Unbox_Any, targetMethod.DeclaringType);
					il.DeclareLocal(targetMethod.DeclaringType);
					il.Emit(OpCodes.Stloc_0);
					il.Emit(OpCodes.Ldloca_S, 0);
				}
				else
					il.Emit(OpCodes.Castclass, targetMethod.DeclaringType);
			}
			var parameters = targetMethod.GetParameters();
			for (var i = 0; i < parameters.Length; i++)
			{
				il.Emit(OpCodes.Ldarg_1);
				/*if (i <= 8)
					il.Emit(ToConstant(i));
				else*/
				il.Emit(OpCodes.Ldc_I4, i);
				il.Emit(OpCodes.Ldelem_Ref);
				if (parameters[i].ParameterType.IsValueType)
					il.Emit(OpCodes.Unbox_Any, parameters[i].ParameterType);
			}
			il.Emit(dynamicMethod.IsStatic ? OpCodes.Call : OpCodes.Callvirt, targetMethod);
			if (targetMethod.ReturnType == typeof (void))
				il.Emit(OpCodes.Ldnull);
			else if (targetMethod.ReturnType.IsValueType)
				il.Emit(OpCodes.Box, targetMethod.ReturnType);
			il.Emit(OpCodes.Ret);
			return (Func<object, object[], object>) dynamicMethod.CreateDelegate(typeof (Func<object, object[], object>));
		}

		private static void EmitNewMethodInvocation(ILGenerator generator, MethodInfo methodInfo, Type interfaceType)
		{
			generator.Emit(OpCodes.Newobj, typeof(MethodInvocation).GetConstructor(Type.EmptyTypes));
			//set name
			generator.Emit(OpCodes.Dup);
			EmitLoadMethodInfo(generator, methodInfo, interfaceType);
			generator.Emit(OpCodes.Stfld, typeof(MethodInvocation).GetField("MethodInfo"));
			//set parameters
			generator.Emit(OpCodes.Dup);
			generator.Emit(OpCodes.Ldc_I4, methodInfo.GetParameters().Length);
			generator.Emit(OpCodes.Newarr, typeof(object));
			var parameters = methodInfo.GetParameters();
			for (int index = 0; index < parameters.Length; index++)
			{
				var parameter = parameters[index];
				generator.Emit(OpCodes.Dup);
				generator.Emit(OpCodes.Ldc_I4, index);
				generator.Emit(OpCodes.Ldarg, index + 1);
				if (parameter.ParameterType.IsValueType)
					generator.Emit(OpCodes.Box, parameter.ParameterType);
				generator.Emit(OpCodes.Stelem, typeof(object));
			}
			generator.Emit(OpCodes.Stfld, typeof(MethodInvocation).GetField("Arguments"));
		}

		private static void EmitLoadMethodInfo(ILGenerator generator, MethodInfo methodInfo, Type interfaceType)
		{
			generator.Emit(OpCodes.Ldtoken, methodInfo);
			generator.Emit(OpCodes.Ldtoken, interfaceType);
			generator.Emit(OpCodes.Call,
						   typeof(MethodBase).GetMethod("GetMethodFromHandle", BindingFlags.Static | BindingFlags.Public, null,
						   new[] { typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle) }, new ParameterModifier[0]));
		}

		public interface IHandler
		{
			object Handle(MethodInvocation invocation);
		}

		public interface IInterceptor
		{
			void Handle(InterceptorArgs args);
		}

		public class InterceptorArgs
		{
			public InterceptorArgs(object proxy, Func<object, object[], object> method, MethodInvocation invocation)
			{
				Proxy = proxy;
				Method = method;
				Invocation = invocation;
			}

			private readonly object Proxy;
			private readonly Func<object, object[], object> Method;
			public MethodInvocation Invocation { get; protected set; }
			public object Result;

			public void Proceed()
			{
				Result = Method(Proxy, Invocation.Arguments);
			}
		}

		private class Adapter : IInterceptor
		{
			public Adapter(IHandler handler)
			{
				this.handler = handler;
			}

			private readonly IHandler handler;

			public void Handle(InterceptorArgs args)
			{
				args.Result = handler.Handle(args.Invocation);
			}
		}

		public class MethodInvocation
		{
			public MethodInfo MethodInfo;
			public object[] Arguments;
		}
	}
}