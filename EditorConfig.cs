using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Termpad;

internal sealed class EditorConfig
{
    public const string DefaultLanguageAuto = "auto";

    public int TabSize { get; private set; } = 4;
    public bool ShowLineNumbers { get; private set; } = true;
    public bool SyntaxHighlighting { get; private set; } = true;
    public bool AutoIndent { get; private set; } = true;
    public int UndoLimit { get; private set; } = 300;
    public int UndoMemoryLimitMb { get; private set; } = 256;
    public int MaxFileSizeMb { get; private set; } = 100;
    public string DefaultLanguage { get; private set; } = DefaultLanguageAuto;

    public string? LoadWarning { get; private set; }
    public string ConfigPath { get; private set; } = "";

    private const string DefaultJson =
"""
{
  // Ширина отступа по Tab (1..16)
  "tabSize": 4,
  // Показывать номера строк
  "showLineNumbers": true,
  // Подсветка синтаксиса
  "syntaxHighlighting": true,
  // Сохранять отступ строки при нажатии Enter
  "autoIndent": true,
  // Сколько шагов хранить в истории отмены (10..10000)
  "undoLimit": 300,
  // Ограничение памяти для истории отмены, МБ (16..4096)
  "undoMemoryLimitMb": 256,
  // Максимальный размер открываемого файла, МБ (1..4096)
  "maxFileSizeMb": 100,
  // Язык по умолчанию: auto, plain, csharp, python, javascript, json, go
  "defaultLanguage": "auto"
}
""";

    public static EditorConfig Load(string? overridePath = null)
    {
        var config = new EditorConfig();
        string path = ResolvePath(overridePath);
        config.ConfigPath = path;

        try
        {
            if (!File.Exists(path))
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, DefaultJson, new UTF8Encoding(false));
            }

            string json = File.ReadAllText(path);
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            var issues = new List<string>();
            config.Apply(document.RootElement, issues);
            if (issues.Count > 0) config.LoadWarning = "Конфиг: " + string.Join("; ", issues);
        }
        catch (Exception ex)
        {
            config.LoadWarning = "Ошибка конфига: " + ex.Message;
        }

        return config;
    }

    private static string ResolvePath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        string? environmentPath = Environment.GetEnvironmentVariable("TERMPAD_CONFIG");
        if (!string.IsNullOrWhiteSpace(environmentPath))
            return Path.GetFullPath(environmentPath);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Termpad",
            "config.json");
    }

    private void Apply(JsonElement root, List<string> issues)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            issues.Add("ожидался объект JSON");
            return;
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in root.EnumerateObject())
            values[property.Name] = property.Value;

        if (values.TryGetValue("tabSize", out JsonElement tabSize))
        {
            if (tabSize.ValueKind == JsonValueKind.Number && tabSize.TryGetInt32(out int size) && size is >= 1 and <= 16)
                TabSize = size;
            else
                issues.Add("tabSize должен быть целым от 1 до 16");
        }

        ShowLineNumbers = ReadBool(values, "showLineNumbers", ShowLineNumbers, issues);
        SyntaxHighlighting = ReadBool(values, "syntaxHighlighting", SyntaxHighlighting, issues);
        AutoIndent = ReadBool(values, "autoIndent", AutoIndent, issues);

        if (values.TryGetValue("undoLimit", out JsonElement undoLimit))
        {
            if (undoLimit.ValueKind == JsonValueKind.Number && undoLimit.TryGetInt32(out int limit) && limit is >= 10 and <= 10000)
                UndoLimit = limit;
            else
                issues.Add("undoLimit должен быть целым от 10 до 10000");
        }

        if (values.TryGetValue("undoMemoryLimitMb", out JsonElement undoMemory))
        {
            if (undoMemory.ValueKind == JsonValueKind.Number && undoMemory.TryGetInt32(out int memory) && memory is >= 16 and <= 4096)
                UndoMemoryLimitMb = memory;
            else
                issues.Add("undoMemoryLimitMb должен быть целым от 16 до 4096");
        }

        if (values.TryGetValue("maxFileSizeMb", out JsonElement maxFileSize))
        {
            if (maxFileSize.ValueKind == JsonValueKind.Number && maxFileSize.TryGetInt32(out int size) && size is >= 1 and <= 4096)
                MaxFileSizeMb = size;
            else
                issues.Add("maxFileSizeMb должен быть целым от 1 до 4096");
        }

        if (values.TryGetValue("defaultLanguage", out JsonElement language))
        {
            if (language.ValueKind == JsonValueKind.String)
            {
                string value = (language.GetString() ?? "").Trim().ToLowerInvariant();
                if (value is "auto" or "plain" or "csharp" or "cs" or "python" or "py"
                    or "javascript" or "js" or "ts" or "json" or "go" or "golang")
                    DefaultLanguage = value;
                else
                    issues.Add("defaultLanguage: допустимо auto, plain, csharp, python, javascript, json, go");
            }
            else
            {
                issues.Add("defaultLanguage должен быть строкой");
            }
        }
    }

    private static bool ReadBool(
        Dictionary<string, JsonElement> values,
        string name,
        bool fallback,
        List<string> issues)
    {
        if (!values.TryGetValue(name, out JsonElement element)) return fallback;

        if (element.ValueKind == JsonValueKind.True) return true;
        if (element.ValueKind == JsonValueKind.False) return false;

        issues.Add(name + " должен быть true или false");
        return fallback;
    }
}
