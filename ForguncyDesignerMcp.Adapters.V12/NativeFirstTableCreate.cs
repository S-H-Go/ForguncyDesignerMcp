using System.Collections;
using System.IO;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// Only the first local root table in a remotely collaborated project is
// eligible. No initialization, DDL or write is performed by this observer.
internal sealed record NativeFirstTableCreate(string Name, string Column, object Root, NativeProcessAppDatabase Database)
{
    internal static bool TryRequest(JsonElement input, out string name, out string column)
    {
        name = column = "";
        if (input.ValueKind != JsonValueKind.Object) return false;
        var fields = input.EnumerateObject().ToArray();
        if (fields.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length ||
            fields.Any(p => p.Name is not ("name" or "columns" or "comment" or "connectionId" or "folderPath")) ||
            !input.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String ||
            !ValidIdentifier(n.GetString()!) || !input.TryGetProperty("columns", out var fieldsNode) ||
            fieldsNode.ValueKind != JsonValueKind.Array || fieldsNode.GetArrayLength() != 1) return false;
        foreach (var field in fields)
            if ((field.Name is "connectionId" or "folderPath") && field.Value.ValueKind != JsonValueKind.Null &&
                (field.Value.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(field.Value.GetString()))) return false;
        var item = fieldsNode[0];
        if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
            !item.TryGetProperty("columnName", out var text) || text.ValueKind != JsonValueKind.String ||
            !ValidIdentifier(text.GetString()!) || !item.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "Text") return false;
        name = n.GetString()!; column = text.GetString()!;
        return !column.Equals("ID", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidIdentifier(string text) => text.Length is > 0 and <= 80 &&
        char.IsAsciiLetter(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
        !text.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesPlan(NativeApiFileCapture capture)
    {
        if (capture is not { Namespace: "table", Method: "createTable", Operation: ModuleOperation.Create, FirstTableCreate: { } reviewed } ||
            capture.Address != new ModuleAddress(ModuleKind.Table, reviewed.Name) ||
            capture.Plan is not { BeforeGroups.Count: 0, ExpectedNewModules: [var expected], NativeChanges: [var change] } ||
            expected != ("Table/" + reviewed.Name, "Table")) return false;
        return change.GetType().GetField("Item2")?.GetValue(change)?.ToString() == "New" &&
            change.GetType().GetField("Item1")?.GetValue(change) is { } token &&
            token.GetType().FullName == "Forguncy.Common.SimpleModule" &&
            (string?)R.Get(token, "ModuleName") == expected.Name && R.Get(token, "ModuleType")?.ToString() == expected.Type;
    }

    internal static bool MatchesSchema(string name, string column, IReadOnlyList<string> modelColumns,
        (IReadOnlyList<(string Kind, string Name, string Owner)> Objects, IReadOnlyList<(string Name, string Type)> Columns, bool EmptyRows) schema)
    {
        if (!schema.EmptyRows || schema.Objects.Count(x => x == ("table", name, name)) != 1 ||
            schema.Objects.Any(x => x != ("table", name, name) && x != ("table", "sqlite_sequence", "sqlite_sequence") &&
                !(x.Kind == "index" && x.Owner == name && x.Name.StartsWith("sqlite_autoindex_" + name + "_", StringComparison.Ordinal))))
            return false;
        if (modelColumns.Count == 0 || modelColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != modelColumns.Count ||
            !modelColumns.SequenceEqual(schema.Columns.Select(x => x.Name), StringComparer.Ordinal))
            return false;
        return schema.Columns.Count(x => x.Name == column &&
            (x.Type.Contains("TEXT", StringComparison.OrdinalIgnoreCase) || x.Type.Contains("CHAR", StringComparison.OrdinalIgnoreCase) ||
             x.Type.Contains("CLOB", StringComparison.OrdinalIgnoreCase))) == 1;
    }

    internal static BridgeException Denied(string text) => new("NativeFirstTableCreateUnverified", text);
}
