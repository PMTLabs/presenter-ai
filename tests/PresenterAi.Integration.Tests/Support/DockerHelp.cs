namespace PresenterAi.Integration.Tests.Support;

internal static class DockerHelp
{
    /// <summary>
    /// Shown whenever Testcontainers cannot reach a Docker daemon. It names the value that actually works on this
    /// project's Windows hosts, because the usual npipe default does not: the daemon runs inside WSL.
    /// </summary>
    public const string Message =
        "Testcontainers could not reach Docker. Set DOCKER_HOST to the reachable Docker daemon and rerun. " +
        "On this project's Windows hosts the daemon lives in WSL and is reached over TCP: " +
        "PowerShell $env:DOCKER_HOST='tcp://localhost:2375', bash export DOCKER_HOST=tcp://localhost:2375. " +
        "Inside WSL or on Linux, unix:///var/run/docker.sock is usually discovered automatically.";
}
