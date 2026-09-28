using System.Diagnostics;

using D2Vpn.Core.Processes;

namespace D2Vpn.Core.Tests.Processes;

/// <summary>
/// Proves the <c>ntdll</c> path really stops a process, against a real one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GamePauseTests"/> covers the policy against a fake, which is where the safety
/// guarantees live and what needs to run on every build. This covers the one thing a fake cannot:
/// that <c>NtSuspendProcess</c> is reachable, that the handle rights asked for are enough, and
/// that a suspended process actually stops doing work.
/// </para>
/// <para>
/// <b>Skipped by default.</b> It spawns processes, takes about eight seconds, and infers the
/// suspension from a file that stops growing - all of which make it too slow and too
/// timing-dependent to sit in a suite that runs constantly. It passed when written.
/// </para>
/// <para>
/// To run it, delete the <c>Skip</c> and:
/// <code>dotnet test --filter "FullyQualifiedName~RealSuspendTests"</code>
/// </para>
/// </remarks>
public class RealSuspendTests
{
    [Fact(Skip = "Slow and timing-dependent; see the remarks to run it on demand.")]
    public void A_suspended_process_stops_doing_work()
    {
        var output = Path.Combine(Path.GetTempPath(), $"d2vpn-suspend-{Guid.NewGuid():N}.txt");

        // ping writes a line a second, so the file size is a cheap proxy for "is it running".
        var shell = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c ping -n 30 127.0.0.1 > \"{output}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        try
        {
            Thread.Sleep(2500);
            Assert.True(new FileInfo(output).Length > 0, "the child should write before suspending");

            // The ping is a child of cmd, and it is the one doing the work.
            using var control = NtProcessControl.Open("PING");

            control.Suspend();
            var atSuspend = new FileInfo(output).Length;

            Thread.Sleep(3000);
            var afterWait = new FileInfo(output).Length;

            Assert.Equal(atSuspend, afterWait);

            control.Resume();
            Thread.Sleep(3000);

            Assert.True(new FileInfo(output).Length > afterWait, "should write again once resumed");
        }
        finally
        {
            try
            {
                if (!shell.HasExited)
                {
                    shell.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            shell.Dispose();

            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
                // A temp file that outlives the test is not worth failing over.
            }
        }
    }
}
