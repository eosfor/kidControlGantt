sealed class CurrentGradesProvider
{
    private readonly CurrentGradesFileReader _reader;

    public CurrentGradesProvider(CurrentGradesFileReader reader)
    {
        _reader = reader;
    }

    public async Task<CurrentGradesResponse> GetCurrentGradesAsync(CancellationToken cancellationToken)
    {
        var result = await _reader.ReadAsync(cancellationToken);
        if (!result.Success)
        {
            throw new AppHttpException(500, result.ErrorMessage ?? "Не удалось прочитать текущие оценки");
        }

        var snapshot = result.Snapshot!;
        return new CurrentGradesResponse(snapshot.UserName, snapshot.AsOf, snapshot.CurrentGrades);
    }
}
