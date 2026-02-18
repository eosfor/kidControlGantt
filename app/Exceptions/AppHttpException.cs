sealed class AppHttpException : Exception
{
    public AppHttpException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
