namespace Yaref92.Events.Sessions;

[Serializable]
internal class TcpConnectionDisconnectedException : Exception
{
    public TcpConnectionDisconnectedException()
    {
    }

    public TcpConnectionDisconnectedException(string? message) : base(message)
    {
    }

    public TcpConnectionDisconnectedException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
