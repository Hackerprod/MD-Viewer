using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;

namespace MDViewer;

public partial class App : Application
{
    private const string MutexName = "MDViewer_SingleInstance_v1";
    private const string PipeName  = "MDViewer_IPC_v1";

    internal static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MDViewer", "error.log");

    private Mutex?      _mutex;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Reset log on each launch
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, $"MDViewer started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        }
        catch { }

        DispatcherUnhandledException += (_, ex) =>
        {
            LogError(ex.Exception);
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            LogError(ex.ExceptionObject as Exception);

        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Ya hay una instancia corriendo — enviarle el archivo y salir
            if (e.Args.Length > 0)
                SendToExistingInstance(e.Args[0]);
            else
                BringExistingInstanceToFront();

            _mutex.Dispose();
            Shutdown();
            return;
        }

        _mainWindow = new MainWindow();
        _mainWindow.Show();

        if (e.Args.Length > 0)
            _mainWindow.OpenFile(e.Args[0]);

        StartPipeServer();
    }

    // ── IPC: cliente (instancia secundaria) ───────────────────────────────────

    private static void SendToExistingInstance(string filePath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, leaveOpen: true);
            writer.WriteLine(filePath);
            writer.Flush();
        }
        catch { /* primary instance not ready yet, ignore */ }
    }

    private static void BringExistingInstanceToFront()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, leaveOpen: true);
            writer.WriteLine(""); // empty message = just bring window to front
            writer.Flush();
        }
        catch { }
    }

    // ── IPC: servidor (instancia primaria) ────────────────────────────────────

    private void StartPipeServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnection();

                    using var reader = new StreamReader(server);
                    server = null; // reader takes ownership
                    var message = reader.ReadLine();

                    Dispatcher.Invoke(() =>
                    {
                        _mainWindow?.Activate();
                        if (_mainWindow?.WindowState == WindowState.Minimized)
                            _mainWindow.WindowState = WindowState.Normal;
                        _mainWindow?.Focus();

                        if (!string.IsNullOrWhiteSpace(message))
                            _mainWindow?.OpenFile(message);
                    });
                }
                catch
                {
                    server?.Dispose();
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name         = "MDViewer.PipeServer"
        };

        thread.Start();
    }

    internal static void LogError(Exception? ex, string? context = null)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {context ?? "Exception"}: {ex}{Environment.NewLine}";
            File.AppendAllText(LogPath, line);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
