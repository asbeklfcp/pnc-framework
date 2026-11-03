using System;

namespace PncCustomEnemies;

// Select a property value, never a similarly spelled string value followed by
// an unrelated object. Kept Unity-free so the actual manifest path is testable.
internal static class ManifestJson
{
    internal static string Delimited(string json, string field, char open, char close)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int start = -1;
        for (int i = 0; i < json.Length; i++)
        {
            if (json[i] != '"') continue;
            int tokenStart = ++i;
            bool escaped = false;
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') break;
            }
            if (i >= json.Length) return null;
            string token = json.Substring(tokenStart, i - tokenStart);
            int value = i + 1;
            while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
            if (value >= json.Length || json[value] != ':' ||
                !string.Equals(token, field, StringComparison.OrdinalIgnoreCase)) continue;
            value++;
            while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
            if (value >= json.Length || json[value] != open) return null;
            start = value;
            break;
        }
        if (start < 0) return null;
        bool inString = false, slash = false;
        int depth = 0;
        for (int i = start; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (slash) slash = false;
                else if (c == '\\') slash = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == open) depth++;
            else if (c == close && --depth == 0) return json.Substring(start, i - start + 1);
        }
        return null;
    }
}
