using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using SteamCN.App.Services;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Storage;
using SteamCN.Translation;

namespace SteamCN.App;

public partial class App : Application
{
	private IServiceProvider? _services;

	private TaskbarIcon? _trayIcon;

	private MainWindow? _mainWindow;

	private FloatingBallWindow? _floatingBall;

	private TranslationOrchestrator? _orchestrator;

	private HotkeyManager? _hotkeyManager;

	private DatabaseManager? _databaseManager;

	public IServiceProvider Services => _services ?? throw new InvalidOperationException("服务未初始化");

	private static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamCNTranslator", "startup.log");

	private static void Log(string message)
	{
		try
		{
			File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
		}
		catch
		{
		}
	}

	public App()
	{
		DispatcherUnhandledException += (object s, DispatcherUnhandledExceptionEventArgs e) =>
		{
			Log($"DispatcherUnhandledException: {e.Exception}");
			MessageBox.Show("发生错误: " + e.Exception.Message + "\n\n" + e.Exception.StackTrace, "SteamCN Translator", MessageBoxButton.OK, MessageBoxImage.Hand);
			e.Handled = true;
		};
		AppDomain.CurrentDomain.UnhandledException += (object s, UnhandledExceptionEventArgs e) =>
		{
			Log($"AppDomain.UnhandledException: {e.ExceptionObject}");
		};
		TaskScheduler.UnobservedTaskException += (object? s, UnobservedTaskExceptionEventArgs e) =>
		{
			Log($"UnobservedTaskException: {e.Exception}");
			e.SetObserved();
		};
	}

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		try
		{
			Log("=== 启动开始 ===");
			Log("步骤1: 构建 DI 容器");
			_services = ServiceConfigurator.Build();
			Log("步骤2: 初始化数据库");
			_databaseManager = (DatabaseManager)_services.GetService(typeof(DatabaseManager));
			_databaseManager.Initialize();
			Log("步骤3: 导入默认术语词典");
			await EnsureDefaultGlossaryAsync((IGlossaryProcessor)_services.GetService(typeof(IGlossaryProcessor)));
			Log("步骤4: 加载设置并配置翻译服务");
			ISettingsManager settingsManager = (ISettingsManager)_services.GetService(typeof(ISettingsManager));
			await settingsManager.LoadAsync();
			TranslationProviderManager providerManager = (TranslationProviderManager)_services.GetService(typeof(TranslationProviderManager));
			ConfigureTranslationProviders(providerManager, settingsManager);
			Log("步骤5: 创建主窗口");
			Resources.MergedDictionaries.Add(AppUi.Load<ResourceDictionary>("UiTheme"));
			using Stream iconStream = typeof(App).Assembly.GetManifestResourceStream("SteamCN.App.Icon.ico")
				?? throw new InvalidOperationException("程序图标资源缺失");
			var icon = new IconBitmapDecoder(iconStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
			BitmapFrame iconFrame = icon.Frames.First(frame => frame.PixelWidth == 32);
			Resources["UiBrandIcon"] = icon.Frames.OrderByDescending(frame => frame.PixelWidth).First();
			_mainWindow = new MainWindow(_services) { Icon = iconFrame };
			_mainWindow.Loaded += OnMainWindowLoaded;
			Log("步骤6: 创建托盘图标");
			SetupTrayIcon(iconFrame);
			Log("步骤7: 启动行为");
			if (settingsManager.Settings.Startup.MinimizeToTray)
			{
				_mainWindow.Hide();
			}
			else
			{
				_mainWindow.Show();
			}
			Log("步骤8: 启动编排器");
			_orchestrator = (TranslationOrchestrator)_services.GetService(typeof(TranslationOrchestrator));
			await _orchestrator.StartAsync();
			Log("步骤9: 注册热键");
			_hotkeyManager = (HotkeyManager)_services.GetService(typeof(HotkeyManager));
			SetupHotkeys(settingsManager.Settings);
			Log("步骤10: 创建悬浮球");
			if (settingsManager.Settings.FloatingBall.Enabled)
			{
				_floatingBall = new FloatingBallWindow(settingsManager, _orchestrator);
				_floatingBall.Show();
			}
			Log("=== 启动完成 ===");
		}
		catch (Exception ex)
		{
			Log($"启动失败: {ex}");
			MessageBox.Show("启动失败: " + ex.Message + "\n\n" + ex.StackTrace, "SteamCN Translator", MessageBoxButton.OK, MessageBoxImage.Hand);
			Shutdown(1);
		}
	}

	private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
	{
		if (_hotkeyManager != null && _mainWindow != null)
		{
			WindowInteropHelper windowInteropHelper = new WindowInteropHelper(_mainWindow);
			_hotkeyManager.Initialize(windowInteropHelper.Handle);
		}
	}

	private void ConfigureTranslationProviders(TranslationProviderManager providerManager, ISettingsManager settingsManager)
	{
		string azureApiKey = SecureStorage.LoadApiKey();
		providerManager.ApplySettings(settingsManager.Settings, azureApiKey);
	}

	private static async Task EnsureDefaultGlossaryAsync(IGlossaryProcessor glossary)
	{
		_ = 1;
		try
		{
			if (glossary.GetTerms().Count <= 0)
			{
				string path = Path.Combine(AppContext.BaseDirectory, "resources", "glossary.default.json");
				if (File.Exists(path))
				{
					await glossary.ImportFromJsonAsync(await File.ReadAllTextAsync(path));
					glossary.RefreshVersion();
				}
			}
		}
		catch
		{
		}
	}

	private void SetupTrayIcon(BitmapFrame icon)
	{
		_trayIcon = new TaskbarIcon
		{
			IconSource = icon,
			ToolTipText = "SteamCN Translator - Steam 外挂翻译"
		};
		_trayIcon.TrayLeftMouseUp += (object s, RoutedEventArgs e) =>
		{
			ShowMainWindow();
		};
		ContextMenu contextMenu = new ContextMenu();
		MenuItem menuItem = new MenuItem
		{
			Header = "显示主窗口"
		};
		menuItem.Click += (object s, RoutedEventArgs e) =>
		{
			ShowMainWindow();
		};
		MenuItem menuItem2 = new MenuItem
		{
			Header = "暂停/恢复翻译"
		};
		menuItem2.Click += (object s, RoutedEventArgs e) =>
		{
			_orchestrator?.TogglePause();
		};
		MenuItem menuItem3 = new MenuItem
		{
			Header = "退出"
		};
		menuItem3.Click += (object s, RoutedEventArgs e) =>
		{
			ExitApplication();
		};
		contextMenu.Items.Add(menuItem);
		contextMenu.Items.Add(menuItem2);
		contextMenu.Items.Add(new Separator());
		contextMenu.Items.Add(menuItem3);
		_trayIcon.ContextMenu = contextMenu;
	}

	private void SetupHotkeys(AppSettings settings)
	{
		if (settings.Hotkeys.Enabled && _hotkeyManager != null)
		{
			_hotkeyManager.HotkeyPressed += OnHotkeyPressed;
			HotkeySettings hotkeys = settings.Hotkeys;
			_hotkeyManager.Register("TogglePause", hotkeys.TogglePause);
			_hotkeyManager.Register("CycleMode", hotkeys.CycleMode);
			_hotkeyManager.Register("HideOverlay", hotkeys.HideOverlay);
			_hotkeyManager.Register("OpenSettings", hotkeys.OpenSettings);
		}
	}

	private void OnHotkeyPressed(string name)
	{
		Dispatcher.Invoke(() =>
		{
			switch (name)
			{
			case "TogglePause":
				_orchestrator?.TogglePause();
				break;
			case "CycleMode":
				_orchestrator?.CycleDisplayMode();
				break;
			case "HideOverlay":
				_orchestrator?.ToggleOverlayVisibility();
				break;
			case "OpenSettings":
				ShowMainWindow();
				break;
			}
		});
	}

	public void ShowMainWindow()
	{
		if (_mainWindow != null)
		{
			_mainWindow.Show();
			_mainWindow.WindowState = WindowState.Normal;
			_mainWindow.Activate();
		}
	}

	public void MinimizeToTray()
	{
		_mainWindow?.Hide();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_floatingBall?.Close();
		_hotkeyManager?.Dispose();
		_orchestrator?.Dispose();
		_trayIcon?.Dispose();
		_databaseManager?.Dispose();
		base.OnExit(e);
	}

	public void ExitApplication()
	{
		Shutdown();
	}

}
