using System.Security.Principal;

namespace D2Vpn.Cli;

/// <summary>Checks whether this process can load the WinDivert driver.</summary>
internal static class Elevation
{
    /// <summary>True when the process is running elevated.</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Prints an explanation and returns false when not elevated.
    /// </summary>
    /// <remarks>
    /// Worth failing loudly and early: without elevation the driver open fails deep inside
    /// WinDivert with a message that does not mention permissions at all.
    /// </remarks>
    public static bool RequireElevated()
    {
        if (IsElevated)
        {
            return true;
        }

        Console.Error.WriteLine("This command needs administrator rights.");
        Console.Error.WriteLine("WinDivert installs and loads a kernel driver, which an unelevated");
        Console.Error.WriteLine("process cannot do. Re-run from an elevated terminal.");
        return false;
    }
}
