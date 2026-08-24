namespace KevinZonda.Terminal.Server.UserAuth;

public sealed class AuthConfigurationException : Exception
{
    public AuthConfigurationException(string message)
        : base(message)
    {
    }

    public AuthConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
