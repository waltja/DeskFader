using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DeskFader.Core;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using SystemIcons = System.Drawing.SystemIcons;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace DeskFader.Settings;

public partial class App : System.Windows.Application
{
    private Mutex? ownerMutex;
    private DeskFaderService? service;
    private MainWindow? mainWindow;
    private NotifyIcon? notificationIcon;
    private bool exiting;

    internal DeskFaderService Service => service ?? throw new InvalidOperationException("DeskFader service is unavailable");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ownerMutex = new Mutex(initiallyOwned: true, @"Local\DeskFader.Settings.ServiceOwner", out var ownsService);
        if (!ownsService)
        {
            ownerMutex.Dispose();
            ownerMutex = null;
            System.Windows.MessageBox.Show("DeskFader is already running.", "DeskFader", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        try
        {
            service = new DeskFaderService(new SettingsStore(), new CoreAudioSessionProvider(), new SerialTransport());
            service.Start();
            mainWindow = new MainWindow(Service);
            mainWindow.Closing += MainWindow_Closing;
            mainWindow.StateChanged += MainWindow_StateChanged;
            CreateNotificationIcon();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "DeskFader", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        mainWindow?.Hide();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (!exiting && mainWindow?.WindowState == WindowState.Minimized) mainWindow.Hide();
    }

    private void CreateNotificationIcon()
    {
        var menu = new ContextMenuStrip();
        var restoreItem = new ToolStripMenuItem("Restore");
        restoreItem.Click += (_, _) => RestoreMainWindow();
        menu.Items.Add(restoreItem);
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);
        notificationIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "DeskFader",
            ContextMenuStrip = menu,
            Visible = true
        };
        notificationIcon.DoubleClick += (_, _) => RestoreMainWindow();
    }

    private void RestoreMainWindow()
    {
        if (mainWindow is null) return;
        mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    private void ExitApplication()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ExitApplication);
            return;
        }
        _ = ExitApplicationAsync();
    }

    private async Task ExitApplicationAsync()
    {
        if (exiting) return;
        exiting = true;
        mainWindow?.Close();
        if (service is not null) await service.StopAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        notificationIcon?.Dispose();
        service?.Dispose();
        if (ownerMutex is not null)
        {
            ownerMutex.ReleaseMutex();
            ownerMutex.Dispose();
        }
        base.OnExit(e);
    }
}
