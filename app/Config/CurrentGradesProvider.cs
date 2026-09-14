using System.Text.Json;

sealed class CurrentGradesProvider
{
    private readonly RuntimeSettings _settings;

    public CurrentGradesProvider(RuntimeSettings settings)
    {
        _settings = settings;
    }

    public async Task<CurrentGradesResponse> GetCurrentGradesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settings.CurrentGradesPath))
        {
            throw new AppHttpException(500, $"Не найден файл текущих оценок: {_settings.CurrentGradesPath}");
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settings.CurrentGradesPath, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AppHttpException(500, "Файл текущих оценок должен содержать JSON-объект");
            }

            var asOf = ReadNullableString(root, "asOf");
            if (!root.TryGetProperty("currentGrades", out var gradesElement)
                || gradesElement.ValueKind != JsonValueKind.Array)
            {
                throw new AppHttpException(500, "Файл текущих оценок должен содержать массив currentGrades");
            }

            var grades = new List<CurrentGradeRow>();
            foreach (var gradeElement in gradesElement.EnumerateArray())
            {
                if (gradeElement.ValueKind != JsonValueKind.Object)
                {
                    throw new AppHttpException(500, "Каждый элемент currentGrades должен быть JSON-объектом");
                }

                var classId = ReadRequiredString(gradeElement, "classId");
                var averagePercentage = ReadNullableDouble(gradeElement, "averagePercentage", classId);
                var mark = ReadNullableString(gradeElement, "mark");
                var missing = ReadRequiredNonNegativeInt(gradeElement, "missing", classId);
                var lastUpdated = ReadNullableString(gradeElement, "lastUpdated");

                grades.Add(new CurrentGradeRow(classId, averagePercentage, mark, missing, lastUpdated));
            }

            return new CurrentGradesResponse(asOf, grades);
        }
        catch (JsonException ex)
        {
            throw new AppHttpException(500, $"Некорректный JSON в файле текущих оценок: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new AppHttpException(500, $"Не удалось прочитать файл текущих оценок: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new AppHttpException(500, $"Нет доступа к файлу текущих оценок: {ex.Message}");
        }
    }

    private static string ReadRequiredString(JsonElement element, string property)
    {
        var value = ReadNullableString(element, property)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppHttpException(500, $"У каждой оценки должно быть непустое поле {property}");
        }

        return value;
    }

    private static string? ReadNullableString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new AppHttpException(500, $"Поле {property} должно быть строкой или null");
        }

        return value.GetString();
    }

    private static double? ReadNullableDouble(JsonElement element, string property, string classId)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
        {
            throw new AppHttpException(500, $"Поле {property} для {classId} должно быть числом или null");
        }

        if (result < 0 || result > 1)
        {
            throw new AppHttpException(500, $"Поле {property} для {classId} должно быть в диапазоне от 0 до 1");
        }

        return result;
    }

    private static int ReadRequiredNonNegativeInt(JsonElement element, string property, string classId)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result)
            || result < 0)
        {
            throw new AppHttpException(500, $"Поле {property} для {classId} должно быть целым неотрицательным числом");
        }

        return result;
    }
}
