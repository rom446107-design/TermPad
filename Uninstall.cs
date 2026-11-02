using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Termpad;

internal static class Uninstall
{
    private const string AppName = "Termpad";
    private const string PathMutexName = @"Local\TermpadUserPath";

    public static void Run(string[] args)
    {
        bool silent = args.Any(argument => argument.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        string installDir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string marker = Path.Combine(installDir, ".termpad");
        string exePath = Path.Combine(installDir, AppName + ".exe");

        if (!IsTermpadInstall(installDir))
        {
            Console.WriteLine("Это не папка установленного Termpad: " + installDir);
            return;
        }

        if (!silent)
        {
            Console.WriteLine("Удаление Termpad");
            Console.WriteLine("Папка: " + installDir);
            Console.Write("Удалить? (y/N): ");
            string? answer = Console.ReadLine();
            if (answer is null || !answer.Trim().ToLowerInvariant().StartsWith("y"))
            {
                Console.WriteLine("Отменено.");
                return;
            }
        }

        RemoveUserPath(Path.Combine(installDir, "bin"));
        TryDeleteFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppName + ".lnk"));

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName,
                throwOnMissingSubKey: false);
        }
        catch
        {
        }

        ScheduleDelete(installDir, marker, exePath);
        Console.WriteLine("Termpad удалён.");
    }

    private static bool IsTermpadInstall(string directory)
    {
        try
        {
            string full = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? root = Path.GetPathRoot(full)?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(Path.Combine(full, ".termpad"))) return true;

            return File.Exists(Path.Combine(full, AppName + ".exe"))
                && File.Exists(Path.Combine(full, "bin", "term.cmd"));
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveUserPath(string binDir)
    {
        using var mutex = new Mutex(false, PathMutexName);
        bool acquired = false;

        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            string? current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            if (string.IsNullOrEmpty(current)) return;

            string normalized = Path.GetFullPath(binDir).TrimEnd('\\');
            string[] parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !string.Equals(part.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            string updated = string.Join(";", parts);
            if (!string.Equals(updated, current, StringComparison.Ordinal))
                Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
        }
        catch
        {
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void ScheduleDelete(string directory, string marker, string exePath)
    {
        try
        {
            string command =
                "for ($i = 0; $i -lt 20; $i++) { " +
                "if (-not ((Test-Path -LiteralPath '" + Escape(marker) + "') -or " +
                "(Test-Path -LiteralPath '" + Escape(exePath) + "'))) { break }; " +
                "try { Remove-Item -LiteralPath '" + Escape(directory) +
                "' -Recurse -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 } }";

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded,
                CreateNoWindow = true,
                UseShellExecute = false
            };

            System.Diagnostics.Process.Start(start);
        }
        catch
        {
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("'", "''");
    }
}
