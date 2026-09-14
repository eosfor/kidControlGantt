using System.Text.Json;

sealed class CurrentGradesFileReader
{
    private readonly RuntimeSettings _settings;

    public CurrentGradesFileReader(RuntimeSettings settings)
    {
        _settings = settings;
    }

    public async Task<CurrentGradesReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settings.CurrentGradesPath))
        {
            return Failure("file_missing", $"Не найден файл текущих оценок: {_settings.CurrentGradesPath}");
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settings.CurrentGradesPath, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure("invalid_root", "Файл текущих оценок должен содержать JSON-объект");
            }

            var userName = ReadNullableString(root, "userName", out var userNameError)?.Trim();
            if (userNameError is not null)
            {
                return userNameError;
            }

            if (string.IsNullOrWhiteSpace(userName))
            {
                return Failure("user_name_missing", "Файл текущих оценок должен содержать непустое поле userName");
            }

            if (!root.TryGetProperty("asOf", out var asOfElement) || asOfElement.ValueKind == JsonValueKind.Null)
            {
                return Failure("as_of_missing", "Файл текущих оценок должен содержать поле asOf");
            }

            if (asOfElement.ValueKind != JsonValueKind.String)
            {
                return Failure("as_of_invalid", "Поле asOf должно быть строкой с датой и временем");
            }

            var asOf = asOfElement.GetString();
            if (string.IsNullOrWhiteSpace(asOf))
            {
                return Failure("as_of_empty", "Поле asOf не должно быть пустым");
            }

            if (!root.TryGetProperty("currentGrades", out var gradesElement)
                || gradesElement.ValueKind != JsonValueKind.Array)
            {
                return Failure("grades_missing", "Файл текущих оценок должен содержать массив currentGrades");
            }

            var grades = new List<CurrentGradeRow>();
            foreach (var gradeElement in gradesElement.EnumerateArray())
            {
                if (gradeElement.ValueKind != JsonValueKind.Object)
                {
                    return Failure("invalid_grade", "Каждый элемент currentGrades должен быть JSON-объектом");
                }

                var classId = ReadNullableString(gradeElement, "classId", out var classIdError)?.Trim();
                if (classIdError is not null)
                {
                    return classIdError;
                }

                if (string.IsNullOrWhiteSpace(classId))
                {
                    return Failure("class_id_missing", "У каждой оценки должно быть непустое поле classId");
                }

                var averagePercentage = ReadNullableDouble(gradeElement, "averagePercentage", classId, out var percentageError);
                if (percentageError is not null)
                {
                    return percentageError;
                }

                var mark = ReadNullableString(gradeElement, "mark", out var markError);
                if (markError is not null)
                {
                    return markError;
                }

                var missing = ReadRequiredNonNegativeInt(gradeElement, "missing", classId, out var missingError);
                if (missingError is not null)
                {
                    return missingError;
                }

                var lastUpdated = ReadNullableString(gradeElement, "lastUpdated", out var lastUpdatedError);
                if (lastUpdatedError is not null)
                {
                    return lastUpdatedError;
                }

                grades.Add(new CurrentGradeRow(classId, averagePercentage, mark, missing, lastUpdated));
            }

            return new CurrentGradesReadResult(
                new CurrentGradesSnapshot(userName, asOf.Trim(), grades),
                null,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return Failure("invalid_json", $"Некорректный JSON в файле текущих оценок: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Failure("io_error", $"Не удалось прочитать файл текущих оценок: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure("access_denied", $"Нет доступа к файлу текущих оценок: {ex.Message}");
        }
    }

    private static string? ReadNullableString(
        JsonElement element,
        string property,
        out CurrentGradesReadResult? error)
    {
        error = null;
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            error = Failure("invalid_field_type", $"Поле {property} должно быть строкой или null");
            return null;
        }

        return value.GetString();
    }

    private static double? ReadNullableDouble(
        JsonElement element,
        string property,
        string classId,
        out CurrentGradesReadResult? error)
    {
        error = null;
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
        {
            error = Failure("invalid_percentage", $"Поле {property} для {classId} должно быть числом или null");
            return null;
        }

        if (result < 0 || result > 1)
        {
            error = Failure("percentage_out_of_range", $"Поле {property} для {classId} должно быть в диапазоне от 0 до 1");
            return null;
        }

        return result;
    }

    private static int ReadRequiredNonNegativeInt(
        JsonElement element,
        string property,
        string classId,
        out CurrentGradesReadResult? error)
    {
        error = null;
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result)
            || result < 0)
        {
            error = Failure("invalid_missing_count", $"Поле {property} для {classId} должно быть целым неотрицательным числом");
            return 0;
        }

        return result;
    }

    private static CurrentGradesReadResult Failure(string code, string message)
        => new(null, code, message);
}
