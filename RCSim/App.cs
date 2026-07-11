using System;
using System.IO;

namespace RCSim
{
    /// <summary>
    /// R/C Desk Pilot on the new stack (issue 17): the shipping Sim executable.
    /// Runs the chained game flow (menu -> aircraft -> scenery -> flight, with
    /// weather, games, recorder, settings). --gametest runs the scripted
    /// verification pass instead.
    /// </summary>
    internal static class App
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool test = Array.IndexOf(args, "--gametest") >= 0;
            string outDir = args.Length > 1 && !args[args.Length - 1].StartsWith("--")
                ? args[args.Length - 1] : Environment.CurrentDirectory;
            return GameShell.Run(FindContentRoot(), test, outDir);
        }

        /// <summary>
        /// The content root is the directory containing RCSim\Aircraft and
        /// RCSim\data: the repo root in a dev tree, or a synthesized layout
        /// next to the executable in an install.
        /// </summary>
        private static string FindContentRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RCDeskPilot.sln")))
                    return dir.FullName;
                if (Directory.Exists(Path.Combine(dir.FullName, "RCSim", "Aircraft")))
                    return dir.FullName;
            }
            throw new InvalidOperationException(
                "Game content not found from " + AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
