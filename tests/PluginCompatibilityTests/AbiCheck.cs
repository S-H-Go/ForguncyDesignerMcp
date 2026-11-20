using System.Reflection;
using System.Reflection.Emit;
using System.IO;

internal static class AbiCheck
{
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(op => op.Value);
    internal static (int Count, string[] Errors) ResolveReferencedMembers(Assembly assembly)
    {
        var failures = new List<string>(); var count = 0;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in assembly.GetTypes())
        {
            var members = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
            if (type.TypeInitializer is { } initializer) members = members.Append(initializer);
            foreach (var method in members.Distinct())
            {
                var bytes = method.GetMethodBody()?.GetILAsByteArray(); if (bytes is null) continue;
                var position = 0;
                while (position < bytes.Length)
                {
                    var offset = position; short code = bytes[position++]; if (code == 254) code = (short)(-512 + bytes[position++]);
                    if (!Opcodes.TryGetValue(code, out var op)) throw new BadImageFormatException("Unknown IL opcode.");
                    switch (op.OperandType)
                    {
                        case OperandType.InlineField:
                        case OperandType.InlineMethod:
                        case OperandType.InlineType:
                        case OperandType.InlineTok:
                            var token = BitConverter.ToInt32(bytes, position); position += 4;
                            try
                            {
                                var types = type.IsGenericType ? type.GetGenericArguments() : null;
                                var methods = method is MethodInfo info && info.IsGenericMethod ? info.GetGenericArguments() : null;
                                if (method.Module.ResolveMember(token, types, methods) is null) throw new MissingMemberException("Null resolved member.");
                                count++;
                            }
                            catch (Exception error) when (error is ArgumentException or TypeLoadException or FileNotFoundException or FileLoadException or MissingMethodException or MissingFieldException or BadImageFormatException or NotSupportedException)
                            {
                                failures.Add(type.FullName + "." + method.Name + " IL_" + offset.ToString("X4") + " " + error.GetType().Name + ": " + error.Message);
                            }
                            break;
                        case OperandType.InlineString:
                        case OperandType.InlineSig:
                        case OperandType.InlineI:
                        case OperandType.InlineBrTarget:
                        case OperandType.ShortInlineR: position += 4; break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR: position += 8; break;
                        case OperandType.InlineVar: position += 2; break;
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineVar: position++; break;
                        case OperandType.InlineSwitch:
                            var length = BitConverter.ToInt32(bytes, position); position += 4 + length * 4; break;
                        case OperandType.InlineNone: break;
                        default: throw new BadImageFormatException("Unexpected IL operand.");
                    }
                    if (position > bytes.Length) throw new BadImageFormatException("Truncated IL body.");
                }
            }
        }
        return (count, failures.ToArray());
    }
}
