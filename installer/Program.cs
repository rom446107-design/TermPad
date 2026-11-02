using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace TermpadSetup;

internal static class Program
{
    private const string AppName = "Termpad";
    private const string DisplayName = "Termpad";
    private static readonly string Version =
        (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "1.0.0").Split('+')[0];
    private const string Publisher = "Roman";
    private const string PayloadResource = "payload.zip";
    private const string MarkerFileName = ".termpad";
    private const string PathMutexName = @"Local\TermpadUserPath";

    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new(0xffff);

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
        }

        var options = new Options(args);
        return options.Uninstall ? Uninstall(options) : Install(options);
    }

    private sealed class Options
    {
        public bool Silent;
        public bool Uninstall;
        public bool NoPath;
        public bool NoShortcut;
        public bool NoRegistry;
        public string? InstallDir;

        public Options(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--silent":
                    case "/s":
                        Silent = true;
                        break;
                    case "--uninstall":
                    case "/uninstall":
                        Uninstall = true;
                        break;
                    case "--no-path":
                        NoPath = true;
                        break;
                    case "--no-shortcut":
                        NoShortcut = true;
                        break;
                    case "--no-registry":
                        NoRegistry = true;
                        break;
                    case "--dir":
                        if (i + 1 < args.Length) InstallDir = args[++i];
                        break;
                }
            }
        }
    }

    private static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        AppName);

    private static int Install(Options options)
    {
        string installDir = Path.GetFullPath(options.InstallDir ?? DefaultInstallDir);
        string binDir = Path.Combine(installDir, "bin");
        string exePath = Path.Combine(installDir, AppName + ".exe");

        if (IsDriveRoot(installDir))
        {
            Console.WriteLine("Нельзя устанавливать в корень диска: " + installDir);
            return 1;
        }

        if (!options.Silent)
        {
            Console.WriteLine("Termpad " + Version + " — установка");
            Console.WriteLine("Папка: " + installDir);
            Console.Write("Продолжить? (Y/n): ");
            string? answer = Console.ReadLine();
            if (answer is not null && answer.Trim().ToLowerInvariant().StartsWith("n"))
            {
                Console.WriteLine("Отменено.");
                return 0;
            }
        }

        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(binDir);

        ExtractPayload(installDir);
        WriteMarker(installDir);
        WriteWrapper(Path.Combine(binDir, "term.cmd"));
        WriteWrapper(Path.Combine(binDir, "nn.cmd"));
        TryDeleteFile(Path.Combine(binDir, "nano.cmd"));
        TryDeleteFile(Path.Combine(installDir, "uninstall.ps1"));

        if (!options.NoPath) AddUserPath(binDir);
        if (!options.NoShortcut) CreateStartMenuShortcut(exePath, installDir);
        if (!options.NoRegistry) RegisterUninstall(installDir, exePath);

        Console.WriteLine("Установлено: " + exePath);
        Console.WriteLine("Команды term и nn доступны из нового терминала.");
        Console.WriteLine("Запуск: term файл.txt");

        if (!options.Silent)
        {
            Console.WriteLine();
            Console.Write("Нажмите Enter для выхода...");
            Console.ReadLine();
        }

        return 0;
    }

    private static int Uninstall(Options options)
    {
        string installDir = Path.GetFullPath(options.InstallDir ?? ReadInstallDir() ?? DefaultInstallDir);
        string binDir = Path.Combine(installDir, "bin");

        if (!options.Silent)
        {
            Console.WriteLine("Удаление Termpad");
            Console.WriteLine("Папка: " + installDir);
            Console.Write("Удалить? (y/N): ");
            string? answer = Console.ReadLine();
            if (answer is null || !answer.Trim().ToLowerInvariant().StartsWith("y"))
            {
                Console.WriteLine("Отменено.");
                return 0;
            }
        }

        if (Directory.Exists(installDir) && !IsTermpadInstall(installDir))
        {
            Console.WriteLine("В папке нет установленного Termpad — удаление отменено: " + installDir);
            return 1;
        }

        RemoveUserPath(binDir);
        TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"));

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName,
                throwOnMissingSubKey: false);
        }
        catch
        {
        }

        if (Directory.Exists(installDir))
        {
            try
            {
                Directory.SetCurrentDirectory(Path.GetTempPath());
                Directory.Delete(installDir, true);
            }
            catch
            {
                ScheduleDelete(installDir);
            }
        }

        Console.WriteLine("Termpad удалён.");

        if (!options.Silent)
        {
            Console.Write("Нажмите Enter для выхода...");
            Console.ReadLine();
        }

        return 0;
    }

    private static void ExtractPayload(string installDir)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource);
        if (stream is null) throw new InvalidOperationException("В установщике нет файлов приложения.");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        string root = Path.GetFullPath(installDir);
        string rootWithSlash = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Некорректный путь в архиве: " + entry.FullName);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void WriteMarker(string installDir)
    {
        File.WriteAllText(
            Path.Combine(installDir, MarkerFileName),
            "Termpad " + Version + "\r\n",
            new UTF8Encoding(false));
    }

    private static void WriteWrapper(string wrapperPath)
    {
        File.WriteAllText(wrapperPath, "@echo off\r\n\"%~dp0..\\Termpad.exe\" %*\r\n", new UTF8Encoding(false));
    }

    private static bool IsDriveRoot(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTermpadInstall(string directory)
    {
        try
        {
            string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsDriveRoot(full)) return false;
            if (File.Exists(Path.Combine(full, MarkerFileName))) return true;

            return File.Exists(Path.Combine(full, AppName + ".exe"))
                && File.Exists(Path.Combine(full, "bin", "term.cmd"));
        }
        catch
        {
            return false;
        }
    }

    private static void WithPathLock(Action action)
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

            action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void AddUserPath(string binDir)
    {
        WithPathLock(() =>
        {
            string normalized = Path.GetFullPath(binDir).TrimEnd('\\');
            string? current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            var parts = (current ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

            if (parts.Any(part => string.Equals(part.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase)))
                return;

            parts.Insert(0, normalized);
            Environment.SetEnvironmentVariable("Path", string.Join(";", parts), EnvironmentVariableTarget.User);
            BroadcastEnvironmentChange();
        });
    }

    private static void RemoveUserPath(string binDir)
    {
        WithPathLock(() =>
        {
            string? current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            if (string.IsNullOrEmpty(current)) return;

            string normalized = Path.GetFullPath(binDir).TrimEnd('\\');
            var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !string.Equals(part.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            string updated = string.Join(";", parts);
            if (!string.Equals(updated, current, StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
                BroadcastEnvironmentChange();
            }
        });
    }

    private static void CreateStartMenuShortcut(string exePath, string installDir)
    {
        try
        {
            string shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = installDir;
            shortcut.IconLocation = exePath + ",0";
            shortcut.Description = "Termpad — консольный редактор";
            shortcut.Save();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ярлык не создан: " + ex.Message);
        }
    }

    private static void RegisterUninstall(string installDir, string exePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName);

        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", exePath);

        string uninstall = "\"" + exePath + "\" --uninstall --silent";
        key.SetValue("UninstallString", uninstall);
        key.SetValue("QuietUninstallString", uninstall);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static string EscapeSingleQuotes(string value)
    {
        return value.Replace("'", "''");
    }

    private static string BuildEncodedCleanup(string directory)
    {
        string full = Path.GetFullPath(directory);
        string marker = Path.Combine(full, MarkerFileName);
        string exe = Path.Combine(full, AppName + ".exe");

        string command =
            "Start-Sleep -Seconds 2; " +
            "if ((Test-Path -LiteralPath '" + EscapeSingleQuotes(marker) + "') -or " +
            "(Test-Path -LiteralPath '" + EscapeSingleQuotes(exe) + "')) { " +
            "Remove-Item -LiteralPath '" + EscapeSingleQuotes(full) + "' -Recurse -Force }";

        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private static string? ReadInstallDir()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
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

    private static void ScheduleDelete(string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + BuildEncodedCleanup(directory),
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch
        {
        }
    }

    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "Environment", SmtoAbortIfHung, 5000, out _);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);
}
