namespace Tests.Fixtures;

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

public class ServerFixture : IDisposable
{
    public Process? ServerProcess { get; private set; }
    public IPEndPoint ServerEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5000);

    public ServerFixture()
    {
        // Locate server project path relative to output directory
        string serverProjDir = Path.Combine(AppContext.BaseDirectory, "../../../../Server");
        string serverExeName = "Server.exe"; // Windows executable

        // Dynamically detect build configuration (Release or Debug)
        string config = AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug";
        string serverPath = Path.Combine(serverProjDir, $"bin/{config}/net10.0", serverExeName);

        if (!File.Exists(serverPath))
        {
            string otherConfig = config == "Release" ? "Debug" : "Release";
            string fallbackPath = Path.Combine(serverProjDir, $"bin/{otherConfig}/net10.0", serverExeName);
            if (File.Exists(fallbackPath))
            {
                serverPath = fallbackPath;
            }
        }

        if (!File.Exists(serverPath))
        {
            string serverCsproj = Path.Combine(serverProjDir, "Server.csproj");
            if (File.Exists(serverCsproj))
            {
                // Build the server project programmatically to ensure the executable exists
                var buildInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{serverCsproj}\" -c {config}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var buildProcess = Process.Start(buildInfo);
                buildProcess?.WaitForExit();
            }
        }

        if (File.Exists(serverPath))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                Arguments = "--port 5000 --no-dashboard",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            ServerProcess = Process.Start(startInfo) ?? throw new Exception("Failed to start server process.");

            // Print server output to assist diagnostics
            ServerProcess.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine("[Server stdout] " + e.Data); };
            ServerProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine("[Server stderr] " + e.Data); };
            ServerProcess.BeginOutputReadLine();
            ServerProcess.BeginErrorReadLine();

            // Give socket binding time
            Thread.Sleep(2000);
        }
    }

    public void Dispose()
    {
        if (ServerProcess != null && !ServerProcess.HasExited)
        {
            try
            {
                ServerProcess.Kill();
                ServerProcess.WaitForExit(3000);
            }
            catch
            {
                // Suppress shutdown exceptions
            }
            finally
            {
                ServerProcess.Dispose();
            }
        }
    }
}
