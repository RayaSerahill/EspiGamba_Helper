using System.Text.Json;

namespace EspiGambaHelper.Core;

public static class ChoiceList
{
    public static string[] Parse(string json, string collectionName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            var collection = root.EnumerateObject().FirstOrDefault(p => p.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase));
            if (collection.Value.ValueKind != JsonValueKind.Undefined) root = collection.Value;
        }
        if (root.ValueKind != JsonValueKind.Array)
            throw new FormatException($"SimpleScratch returned an unsupported {collectionName} list.");
        var names = new List<string>();
        foreach (var item in root.EnumerateArray())
        {
            string? name = null;
            if (item.ValueKind == JsonValueKind.String) name = item.GetString();
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var property = item.EnumerateObject().FirstOrDefault(p => p.Name.Equals("name", StringComparison.OrdinalIgnoreCase));
                if (property.Value.ValueKind == JsonValueKind.String) name = property.Value.GetString();
            }
            if (string.IsNullOrWhiteSpace(name)) throw new FormatException($"SimpleScratch returned a {collectionName} entry without a name.");
            names.Add(name);
        }
        return names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
