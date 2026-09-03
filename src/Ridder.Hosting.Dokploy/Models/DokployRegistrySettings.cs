namespace Ridder.Hosting.Dokploy.Models;

internal enum DokployRegistryMode
{
    SelfHosted,
    Hosted
}

internal sealed class DokployResolvedRegistrySettings
{
    public DokployResolvedRegistrySettings(DokployRegistryMode mode, string registryUrl, string username, string password, string registryType)
    {
        Mode = mode;
        RegistryUrl = registryUrl;
        Username = username;
        Password = password;
        RegistryType = registryType;
    }

    public DokployRegistryMode Mode { get; }
    public string RegistryUrl { get; }
    public string Username { get; }
    public string Password { get; }
    public string RegistryType { get; }
}
