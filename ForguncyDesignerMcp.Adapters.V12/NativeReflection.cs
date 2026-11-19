using System.Reflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// The fixed adapter calls these helpers. No type/member names are accepted from wire input.
internal static class NativeReflection
{
    internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static object? Get(object value, string name) =>
        (value.GetType().GetProperty(name, Flags) ?? throw new MissingMemberException(value.GetType().FullName, name)).GetValue(value);
    internal static void Set(object value, string name, object? incoming) =>
        (value.GetType().GetProperty(name, Flags) ?? throw new MissingMemberException(value.GetType().FullName, name)).SetValue(value, incoming);
    internal static object? Call(object value, string name, params object?[] args) => Invoke(value.GetType(), value, name, args);
    internal static object? Static(Type type, string name, params object?[] args) => Invoke(type, null, name, args);
    private static object? Invoke(Type type, object? value, string name, object?[] args)
    {
        var methods = type.GetMethods(Flags).Where(m => m.Name == name && !m.ContainsGenericParameters && m.IsStatic == (value is null) &&
            m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) =>
                args[i] is null ? !p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) is not null :
                p.ParameterType.IsInstanceOfType(args[i])).All(x => x)).ToArray();
        if (methods.Length != 1) throw new MissingMethodException(type.FullName, name);
        try { return methods[0].Invoke(value, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
