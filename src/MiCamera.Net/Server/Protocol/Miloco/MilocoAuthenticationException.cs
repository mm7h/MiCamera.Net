namespace MiCamera.Net.Server.Protocol.Miloco;

internal sealed class MilocoAuthenticationException : Exception
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
