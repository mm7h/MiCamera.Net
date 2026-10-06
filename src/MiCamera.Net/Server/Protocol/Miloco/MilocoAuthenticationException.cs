namespace MiCamera.Net.Server.Protocol.Miloco;

public sealed class MilocoAuthenticationException : Exception
{
    public MilocoAuthenticationException(string message)
        : base(message)
    {
    }

    public MilocoAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
