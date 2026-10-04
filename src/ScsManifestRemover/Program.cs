using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ScsManifestRemover
{
    internal static class Program
    {
        private const int ExitPatched = 0;
        private const int ExitNotFound = 1;
        private const int ExitError = 2;

        private static int Main(string[] args)
        {
            string target = "manifest.sii";
            bool dryRun = false, noBackup = false, noPause = false;
            var files = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-p":
                    case "--path":
                        if (i + 1 >= args.Length) return Fail("--path needs a value.");
                        target = args[++i];
                        break;
                    case "-n":
                    case "--dry-run": dryRun = true; break;
                    case "--no-backup": noBackup = true; break;
                    case "--no-pause": noPause = true; break;
                    case "-h":
                    case "--help":
                    case "/?":
                        PrintUsage();
                        return ExitPatched;
                    default:
                        if (args[i].StartsWith("-")) return Fail("Unknown option: " + args[i]);
                        files.Add(args[i]);
                        break;
                }
            }

            if (files.Count == 0)
            {
                PrintUsage();
                PauseIfOwnConsole(noPause);
                return ExitError;
            }

            int exitCode = ExitPatched;
            foreach (string file in files)
            {
                int code = ProcessFile(file, target, dryRun, noBackup);
                exitCode = Math.Max(exitCode, code);
                Console.WriteLine();
            }

            PauseIfOwnConsole(noPause);
            return exitCode;
        }

        private static int ProcessFile(string file, string target, bool dryRun, bool noBackup)
        {
            Console.WriteLine(file);
            try
            {
                string full = Path.GetFullPath(file);
                if (!File.Exists(full)) return Fail("File not found.");

                // Check first, so the backup is only made for archives that will change.
                PatchResult check = HashFsPatcher.Patch(full, target, true);
                if (check.PatchedEntries == 0 || dryRun)
                {
                    foreach (string line in check.Log) Console.WriteLine("  " + line);
                    if (check.PatchedEntries == 0)
                    {
                        Console.WriteLine("  NOT FOUND: no entry for " + target + ". File left unchanged.");
                        return ExitNotFound;
                    }
                    Console.WriteLine("  DRY RUN: " + check.PatchedEntries + " entry(ies) would be hidden. File left unchanged.");
                    return ExitPatched;
                }

                if (!noBackup)
                {
                    string backup = full + ".backup";
                    if (File.Exists(backup))
                        Console.WriteLine("  Backup already exists, not overwriting: " + backup);
                    else
                    {
                        File.Copy(full, backup);
                        Console.WriteLine("  Backup: " + backup);
                    }
                }

                PatchResult result = HashFsPatcher.Patch(full, target, false);
                foreach (string line in result.Log) Console.WriteLine("  " + line);
                Console.WriteLine("  DONE: " + result.PatchedEntries + " entry(ies) hidden. The game will no longer see " + target + ".");
                return ExitPatched;
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static int Fail(string message)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ERROR: " + message);
            Console.ForegroundColor = old;
            return ExitError;
        }

        private static void PrintUsage()
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Console.WriteLine("ScsManifestRemover " + version);
            Console.WriteLine("Hides a file (manifest.sii by default) inside an ETS2/ATS HashFS .scs archive");
            Console.WriteLine("without extracting it. The original is backed up as <file>.backup.");
            Console.WriteLine();
            Console.WriteLine("Usage: ScsManifestRemover <file.scs> [more files...] [options]");
            Console.WriteLine("       or drag and drop .scs files onto the exe.");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -p, --path <path>  file to hide, relative to the archive root (default: manifest.sii)");
            Console.WriteLine("  -n, --dry-run      report what would change without writing anything");
            Console.WriteLine("      --no-backup    do not create <file>.backup");
            Console.WriteLine("      --no-pause     do not wait for a key press when started by double-click");
            Console.WriteLine();
            Console.WriteLine("Exit codes: 0 = hidden (or dry run found it), 1 = not found, 2 = error.");
        }

        // Keep the window open when started by double-click or drag and drop,
        // i.e. when this process is the only one attached to its console.
        private static void PauseIfOwnConsole(bool noPause)
        {
            if (noPause || Console.IsInputRedirected) return;
            try
            {
                if (GetConsoleProcessList(new uint[2], 2) != 1) return;
            }
            catch (Exception)
            {
                return; // not on Windows
            }
            Console.WriteLine("Press any key to close...");
            Console.ReadKey(true);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
    }
}
