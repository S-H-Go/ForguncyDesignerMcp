using System.Reflection;
using System.Runtime.ExceptionServices;

namespace ForguncyDesignerMcp.Adapters.V11;

// These helpers are used only by the fixed, fingerprinted native adapter. They are
// deliberately not registered as platform APIs and never accept CLR names from MCP.
internal static class NativeV11Reflection
{
    internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.Instance | BindingFlags.Static;

    internal static object? Get(object instance, string name)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
            if (property is not null) return Invoke(() => property.GetValue(instance));
        }
        return null;
    }

    internal static void Set(object instance, string name, object? value)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
            if (property is null) continue;
            if (property.GetSetMethod(true) is null)
                throw new MissingMemberException(instance.GetType().FullName, name + " setter");
            Invoke(() => { property.SetValue(instance, value); return (object?)null; });
            return;
        }
        throw new MissingMemberException(instance.GetType().FullName, name);
    }

    internal static MethodInfo Method(Type type, string name, params object?[] arguments)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var candidates = current.GetMethods(Flags | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == name && !method.ContainsGenericParameters &&
                    method.GetParameters().Length == arguments.Length &&
                    method.GetParameters().Select((parameter, index) => Compatible(parameter.ParameterType, arguments[index])).All(value => value))
                .ToArray();
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length <= 1) continue;
            var exact = candidates.Where(method => method.GetParameters().Select((parameter, index) =>
                arguments[index] is not null && parameter.ParameterType == arguments[index]!.GetType()).All(value => value)).ToArray();
            if (exact.Length == 1) return exact[0];
            throw new AmbiguousMatchException(type.FullName + "." + name);
        }
        throw new MissingMethodException(type.FullName, name);
    }

    internal static object? Call(object instance, string name, params object?[] arguments) =>
        Invoke(() => Method(instance.GetType(), name, arguments).Invoke(instance, arguments));

    internal static object? Static(Type type, string name, params object?[] arguments)
    {
        var method = Method(type, name, arguments);
        if (!method.IsStatic) throw new MissingMethodException(type.FullName, name + " static");
        return Invoke(() => method.Invoke(null, arguments));
    }

    private static bool Compatible(Type type, object? value) => value is null
        ? !type.IsValueType || Nullable.GetUnderlyingType(type) is not null
        : type.IsInstanceOfType(value);

    private static T Invoke<T>(Func<T> action)
    {
        try { return action(); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
