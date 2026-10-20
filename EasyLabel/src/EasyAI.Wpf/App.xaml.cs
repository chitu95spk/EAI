using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyAI.Desktop.Services;
using EasyAI.Desktop.ViewModels;

namespace EasyAI.Desktop;

public partial class App : Application
{
    private SettingsStore? _store;
    private MainViewModel? _viewModel;
    private ImagePreviewLoader? _loader;
    public EasyLabel.Commands.CommandRegistry CommandRegistry { get; } = new EasyLabel.Commands.CommandRegistry();
    [System.Diagnostics.CodeAnalysis.MaybeNull]
    public EasyAI.Desktop.Commands.WpfCommandHost CommandHost { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            StartupOptions options = StartupOptions.Parse(e.Args);
            _store = CreateStore(options);
            bool settingsExisted = _store.SettingsFileExists;
            AppSettings settings = _store.LoadSettings();
            string uiMode = EasyModeRules.ResolveMode(settings.UiMode, settingsExisted);
            if (!string.Equals(settings.UiMode, uiMode, StringComparison.Ordinal))
            {
                settings.UiMode = uiMode;
                _store.SaveSettings(settings);
            }

            ApplyCulture(options.Culture ?? settings.UiCulture ?? "vi");
            ThemeService.Palette = SettingsAccessor.PaletteOf(settings);
            ThemeService.Apply(settings.Theme, null);

            _loader = new ImagePreviewLoader();
            _viewModel = new MainViewModel(_store, settings, options, new EngineStatusService(), _loader);
            CommandHost = new EasyAI.Desktop.Commands.WpfCommandHost(_viewModel, Dispatcher, CommandRegistry);

            MainWindow window = new MainWindow(_viewModel, _store);
            MainWindow = window;
            window.Show();

            _ = _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            try
            {
                _store?.Log("Startup failed: " + ex);
            }
            catch (Exception)
            {
                // Logging must never block shutdown.
            }

            System.Windows.MessageBox.Show(ex.Message, Strings.UnhandledErrorTitle);
            Shutdown(1);
        }
    }

    /// <summary>Requested directory, else the default one, else a store that never persists.</summary>
    private static SettingsStore CreateStore(StartupOptions options)
    {
        try
        {
            SettingsStore store = new SettingsStore(SettingsStore.ResolveDirectory(options));
            store.LoadSettings();
            return store;
        }
        catch (Exception)
        {
            // Fall through to the default directory.
        }

        options.Warnings.Add(Strings.StartupSettingsUnavailable);
        try
        {
            return new SettingsStore(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyAI"));
        }
        catch (Exception)
        {
            return SettingsStore.CreateInMemory();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        _loader?.Dispose();
        base.OnExit(e);
    }

    private static void ApplyCulture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            Strings.Culture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Unknown culture name: keep the OS culture.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _store?.Log("Unhandled: " + e.Exception);
        e.Handled = true;
        if (_viewModel != null)
        {
            _viewModel.ReportUnhandled(e.Exception);
        }
        else
        {
            System.Windows.MessageBox.Show(e.Exception.Message, Strings.UnhandledErrorTitle);
        }
    }
}
