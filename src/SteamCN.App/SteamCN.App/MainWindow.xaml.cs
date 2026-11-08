using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamCN.App.Services;
using SteamCN.Core.Enums;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Models;
using SteamCN.Core.Pipeline;
using SteamCN.Translation;
using SteamCN.Windows;

namespace SteamCN.App;

public class MainWindow : Window
{
	private readonly IServiceProvider _services;

	private readonly TranslationOrchestrator _orchestrator;

	private readonly ISettingsManager _settingsManager;

	private readonly ISteamWindowManager _windowManager;

	private readonly TranslationProviderManager _providerManager;

	private readonly LocalTranslatorProvider _localProvider;

	private readonly LocalEngineInstaller _localInstaller;

	private readonly ICompatibilityManager _compatibilityManager;

	private readonly IOverlayRenderer _overlayRenderer;

	private readonly ITranslationMemory _translationMemory;

	private readonly DispatcherTimer _statsTimer;

	private bool _initialized;

	private bool _loadingSettings;

	private DateTime _lastTranslationTime = DateTime.MinValue;

	private int _translationSuccessCount;

	private int _translationFailCount;

	private long _lastTranslateMs = -1L;

	private string? _lastTranslationError;

	private DateTime _lastErrorTime = DateTime.MinValue;

	private PipelineDiagnostics? _lastDiag;

	private DateTime _lastDiagTime = DateTime.MinValue;

	internal Rectangle StatusDot;

	internal TextBlock StatusText;

	internal TextBlock DetailStatus;

	internal Button BtnToggle;

	internal TextBlock BoundWindowText;

	internal Button BtnRescan;

	internal TextBlock StatusSteam;

	internal TextBlock StatusEngine;

	internal TextBlock StatusPackages;

	internal TextBlock StatusTranslation;

	internal Button BtnToggleDiag;

	internal StackPanel DiagPanel;

	internal TextBlock DiagCapture;

	internal TextBlock DiagUia;

	internal TextBlock DiagOcr;

	internal TextBlock DiagLang;

	internal TextBlock DiagQueue;

	internal TextBlock DiagEngine;

	internal TextBlock DiagResults;

	internal TextBlock DiagOverlay;

	internal TextBlock DiagScanStatus;

	internal TextBlock DiagLastScan;

	internal TextBlock DiagLastCapture;

	internal TextBlock DiagRecovery;

	internal TextBlock DiagDetail;

	internal RadioButton RbEngineGoogle;

	internal RadioButton RbEngineBing;

	internal RadioButton RbEngineLocal;

	internal RadioButton RbEngineLibre;

	internal RadioButton RbEngineAzure;

	internal CheckBox CbAutoFallback;

	internal Border PanelGoogle;

	internal CheckBox CbGoogleEnabled;

	internal Button BtnTestGoogle;

	internal TextBlock GoogleStatus;

	internal Border PanelBing;

	internal CheckBox CbBingEnabled;

	internal Button BtnTestBing;

	internal TextBlock BingStatus;

	internal Border PanelLocal;

	internal TextBlock LocalEngineStatus;

	internal Button BtnInstallEngine;

	internal Button BtnRepairEngine;

	internal ProgressBar EngineProgress;

	internal TextBlock EngineProgressText;

	internal TextBlock InstalledPackagesText;

	internal Button BtnInstallEnZh;

	internal Button BtnInstallJaZh;

	internal Button BtnInstallKoZh;

	internal TextBlock PackageProgressText;

	internal Border PanelLibre;

	internal TextBox TxtLibreUrl;

	internal PasswordBox TxtLibreKey;

	internal Button BtnSaveLibre;

	internal TextBlock LibreStatus;

	internal Border PanelAzure;

	internal PasswordBox TxtApiKey;

	internal TextBox TxtRegion;

	internal Button BtnSaveKey;

	internal TextBlock KeyStatus;

	internal RadioButton RbOriginal;

	internal RadioButton RbBilingual;

	internal RadioButton RbChinese;

	internal RadioButton RbLayoutSide;

	internal RadioButton RbLayoutOverlay;

	internal RadioButton RbLayoutAdaptive;

	internal Slider SliderOverlayOpacity;

	internal TextBlock LblOverlayOpacity;

	internal Slider SliderFontScale;

	internal TextBlock LblFontScale;

	internal Slider SliderMaxWidth;

	internal TextBlock LblMaxWidth;

	internal Slider SliderBilingualGap;

	internal TextBlock LblBilingualGap;

	internal CheckBox CbShowOriginal;

	internal CheckBox CbWorkshop;

	internal CheckBox CbReviews;

	internal CheckBox CbCommunity;

	internal CheckBox CbStore;

	internal CheckBox CbGuides;

	internal TextBlock StatRequests;

	internal TextBlock StatCharacters;

	internal TextBlock StatCache;

	internal TextBlock StatSuccess;

	internal TextBlock StatFail;

	internal TextBlock StatAvgMs;

	internal Button BtnExportDiag;

	internal Button BtnExit;

	public MainWindow(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
		_orchestrator = services.GetRequiredService<TranslationOrchestrator>();
		_settingsManager = services.GetRequiredService<ISettingsManager>();
		_windowManager = services.GetRequiredService<ISteamWindowManager>();
		_providerManager = services.GetRequiredService<TranslationProviderManager>();
		_localProvider = services.GetRequiredService<LocalTranslatorProvider>();
		_localInstaller = _localProvider.Installer;
		_compatibilityManager = services.GetRequiredService<ICompatibilityManager>();
		_overlayRenderer = services.GetRequiredService<IOverlayRenderer>();
		_translationMemory = services.GetRequiredService<ITranslationMemory>();
		_statsTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(3.0)
		};
		_statsTimer.Tick += (object? s, EventArgs e) =>
		{
			UpdateStatistics();
		};
		_orchestrator.RunningStateChanged += OnRunningStateChanged;
		_orchestrator.StatusMessage += OnStatusMessage;
		_orchestrator.ErrorOccurred += OnErrorOccurred;
		_windowManager.WindowBound += OnWindowBound;
		_windowManager.WindowUnbound += OnWindowUnbound;
		_orchestrator.Scheduler.TranslationCompleted += OnTranslationCompleted;
		_orchestrator.Scheduler.PipelineDiagnostics += OnPipelineDiagnostics;
		LoadSettingsToUi();
		_overlayRenderer.SetMode(_settingsManager.Settings.DisplayMode);
		BtnRescan.ToolTip = "返回 Steam 并重新核验当前页面";
		if (_overlayRenderer is OverlayRenderer overlayRenderer)
		{
			overlayRenderer.ApplyOverlaySettings(_settingsManager.Settings.Overlay);
		}
		UpdateBoundWindowsDisplay();
		UpdateRunningState(_orchestrator.IsRunning);
		UpdateStatistics();
		_statsTimer.Start();
		_initialized = true;
	}

	private void LoadSettingsToUi()
	{
		_loadingSettings = true;
		try
		{
			AppSettings settings = _settingsManager.Settings;
			RbEngineGoogle.IsChecked = settings.Translation.Engine == TranslationEngine.Google;
			RbEngineBing.IsChecked = settings.Translation.Engine == TranslationEngine.Bing;
			RbEngineLocal.IsChecked = settings.Translation.Engine == TranslationEngine.Local;
			RbEngineLibre.IsChecked = settings.Translation.Engine == TranslationEngine.LibreTranslate;
			RbEngineAzure.IsChecked = settings.Translation.Engine == TranslationEngine.Azure;
			UpdateEnginePanelVisibility(settings.Translation.Engine);
			CbAutoFallback.IsChecked = settings.Translation.AutoFallback;
			CbGoogleEnabled.IsChecked = settings.Translation.GoogleEnabled;
			CbBingEnabled.IsChecked = settings.Translation.BingEnabled;
			TxtLibreUrl.Text = settings.Translation.LibreTranslateUrl ?? "";
			TxtLibreKey.Password = settings.Translation.LibreTranslateApiKey ?? "";
			TxtRegion.Text = (string.IsNullOrEmpty(settings.Translation.AzureRegion) ? "global" : settings.Translation.AzureRegion);
			if (SecureStorage.HasApiKey)
			{
				KeyStatus.Text = "API 密钥已配置";
				KeyStatus.Foreground = AppUi.Brush("UiSuccess");
			}
			UpdateLocalEngineStatus();
			UpdateComponentStatus();
			RbOriginal.IsChecked = settings.DisplayMode == DisplayMode.Original;
			RbBilingual.IsChecked = settings.DisplayMode == DisplayMode.Bilingual;
			RbChinese.IsChecked = settings.DisplayMode == DisplayMode.Chinese;
			RbLayoutSide.IsChecked = settings.Overlay.LayoutMode == LayoutMode.SideBySide;
			RbLayoutOverlay.IsChecked = settings.Overlay.LayoutMode == LayoutMode.Overlay;
			RbLayoutAdaptive.IsChecked = settings.Overlay.LayoutMode == LayoutMode.Adaptive;
			SliderOverlayOpacity.Value = settings.Overlay.OverlayOpacity;
			SliderFontScale.Value = settings.Overlay.FontScale;
			SliderMaxWidth.Value = settings.Overlay.MaxTranslationWidth;
			SliderBilingualGap.Value = settings.Overlay.BilingualGap;
			CbShowOriginal.IsChecked = settings.Overlay.ShowOriginal;
			UpdateLayoutLabels();
			CbWorkshop.IsChecked = settings.Areas.Workshop;
			CbReviews.IsChecked = settings.Areas.Reviews;
			CbCommunity.IsChecked = settings.Areas.Community;
			CbStore.IsChecked = settings.Areas.Store;
			CbGuides.IsChecked = settings.Areas.Guides;
		}
		finally
		{
			_loadingSettings = false;
		}
	}

	private async Task SaveSettingsFromUiAsync()
	{
		AppSettings settings = _settingsManager.Settings;
		if (RbEngineGoogle.IsChecked == true)
		{
			settings.Translation.Engine = TranslationEngine.Google;
		}
		else if (RbEngineBing.IsChecked == true)
		{
			settings.Translation.Engine = TranslationEngine.Bing;
		}
		else if (RbEngineLocal.IsChecked == true)
		{
			settings.Translation.Engine = TranslationEngine.Local;
		}
		else if (RbEngineLibre.IsChecked == true)
		{
			settings.Translation.Engine = TranslationEngine.LibreTranslate;
		}
		else if (RbEngineAzure.IsChecked == true)
		{
			settings.Translation.Engine = TranslationEngine.Azure;
		}
		settings.Translation.AutoFallback = CbAutoFallback.IsChecked == true;
		settings.Translation.GoogleEnabled = CbGoogleEnabled.IsChecked == true;
		settings.Translation.BingEnabled = CbBingEnabled.IsChecked == true;
		if (RbOriginal.IsChecked == true)
		{
			settings.DisplayMode = DisplayMode.Original;
		}
		else if (RbBilingual.IsChecked == true)
		{
			settings.DisplayMode = DisplayMode.Bilingual;
		}
		else if (RbChinese.IsChecked == true)
		{
			settings.DisplayMode = DisplayMode.Chinese;
		}
		if (RbLayoutSide.IsChecked == true)
		{
			settings.Overlay.LayoutMode = LayoutMode.SideBySide;
		}
		else if (RbLayoutOverlay.IsChecked == true)
		{
			settings.Overlay.LayoutMode = LayoutMode.Overlay;
		}
		else
		{
			settings.Overlay.LayoutMode = LayoutMode.Adaptive;
		}
		settings.Overlay.OverlayOpacity = SliderOverlayOpacity.Value;
		settings.Overlay.FontScale = SliderFontScale.Value;
		settings.Overlay.MaxTranslationWidth = (int)SliderMaxWidth.Value;
		settings.Overlay.BilingualGap = (int)SliderBilingualGap.Value;
		settings.Overlay.ShowOriginal = CbShowOriginal.IsChecked == true;
		settings.Areas.Workshop = CbWorkshop.IsChecked == true;
		settings.Areas.Reviews = CbReviews.IsChecked == true;
		settings.Areas.Community = CbCommunity.IsChecked == true;
		settings.Areas.Store = CbStore.IsChecked == true;
		settings.Areas.Guides = CbGuides.IsChecked == true;
		settings.Translation.AzureRegion = TxtRegion.Text.Trim();
		await _settingsManager.SaveAsync();
	}

	private void OnToggleClick(object sender, RoutedEventArgs e)
	{
		if (_orchestrator.IsRunning)
		{
			_orchestrator.Stop();
		}
		else
		{
			StartOrchestratorAsync();
		}
	}

	private async void OnDisplayModeChanged(object sender, RoutedEventArgs e)
	{
		if (_initialized && sender is RadioButton { IsChecked: var isChecked } radioButton && isChecked == true)
		{
			DisplayMode displayMode = ((radioButton != RbOriginal) ? ((radioButton == RbBilingual) ? DisplayMode.Bilingual : DisplayMode.Chinese) : DisplayMode.Original);
			_overlayRenderer.SetMode(displayMode);
			_settingsManager.Settings.DisplayMode = displayMode;
			await _settingsManager.SaveAsync();
		}
	}

	private void UpdateLayoutLabels()
	{
		LblOverlayOpacity.Text = $"{SliderOverlayOpacity.Value * 100.0:F0}%";
		LblFontScale.Text = $"{SliderFontScale.Value:F1}x";
		LblMaxWidth.Text = $"{(int)SliderMaxWidth.Value}px";
		LblBilingualGap.Text = $"{(int)SliderBilingualGap.Value}px";
	}

	private async void OnLayoutModeChanged(object sender, RoutedEventArgs e)
	{
		if (_initialized && !_loadingSettings && sender is RadioButton { IsChecked: var isChecked } radioButton && isChecked == true)
		{
			LayoutMode layoutMode = ((radioButton != RbLayoutSide) ? ((radioButton == RbLayoutOverlay) ? LayoutMode.Overlay : LayoutMode.Adaptive) : LayoutMode.SideBySide);
			_overlayRenderer.SetLayoutMode(layoutMode);
			_settingsManager.Settings.Overlay.LayoutMode = layoutMode;
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnOverlayOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_initialized && !_loadingSettings)
		{
			UpdateLayoutLabels();
			_settingsManager.Settings.Overlay.OverlayOpacity = SliderOverlayOpacity.Value;
			_overlayRenderer.ApplyOverlaySettings(_settingsManager.Settings.Overlay);
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnFontScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_initialized && !_loadingSettings)
		{
			UpdateLayoutLabels();
			_settingsManager.Settings.Overlay.FontScale = SliderFontScale.Value;
			_overlayRenderer.ApplyOverlaySettings(_settingsManager.Settings.Overlay);
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnMaxWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_initialized && !_loadingSettings)
		{
			UpdateLayoutLabels();
			_settingsManager.Settings.Overlay.MaxTranslationWidth = (int)SliderMaxWidth.Value;
			_overlayRenderer.ApplyOverlaySettings(_settingsManager.Settings.Overlay);
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnBilingualGapChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_initialized && !_loadingSettings)
		{
			UpdateLayoutLabels();
			_settingsManager.Settings.Overlay.BilingualGap = (int)SliderBilingualGap.Value;
			_overlayRenderer.ApplyOverlaySettings(_settingsManager.Settings.Overlay);
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnShowOriginalChanged(object sender, RoutedEventArgs e)
	{
		if (_initialized && !_loadingSettings)
		{
			_settingsManager.Settings.Overlay.ShowOriginal = CbShowOriginal.IsChecked == true;
			_overlayRenderer.ApplyOverlaySettings(_settingsManager.Settings.Overlay);
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnAreaToggled(object sender, RoutedEventArgs e)
	{
		if (_initialized)
		{
			_settingsManager.Settings.Areas.Workshop = CbWorkshop.IsChecked == true;
			_settingsManager.Settings.Areas.Reviews = CbReviews.IsChecked == true;
			_settingsManager.Settings.Areas.Community = CbCommunity.IsChecked == true;
			_settingsManager.Settings.Areas.Store = CbStore.IsChecked == true;
			_settingsManager.Settings.Areas.Guides = CbGuides.IsChecked == true;
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnEngineChanged(object sender, RoutedEventArgs e)
	{
		if (_initialized && sender is RadioButton { IsChecked: var isChecked } radioButton && isChecked == true)
		{
			TranslationEngine engine = ((radioButton != RbEngineGoogle) ? ((radioButton == RbEngineBing) ? TranslationEngine.Bing : ((radioButton == RbEngineLocal) ? TranslationEngine.Local : ((radioButton == RbEngineLibre) ? TranslationEngine.LibreTranslate : TranslationEngine.Azure))) : TranslationEngine.Google);
			UpdateEnginePanelVisibility(engine);
			_providerManager.SwitchEngine(engine);
			_settingsManager.Settings.Translation.Engine = engine;
			await _settingsManager.SaveAsync();
		}
	}

	private void UpdateEnginePanelVisibility(TranslationEngine engine)
	{
		PanelGoogle.Visibility = ((engine != TranslationEngine.Google) ? Visibility.Collapsed : Visibility.Visible);
		PanelBing.Visibility = ((engine != TranslationEngine.Bing) ? Visibility.Collapsed : Visibility.Visible);
		PanelLocal.Visibility = ((engine != TranslationEngine.Local) ? Visibility.Collapsed : Visibility.Visible);
		PanelLibre.Visibility = ((engine != TranslationEngine.LibreTranslate) ? Visibility.Collapsed : Visibility.Visible);
		PanelAzure.Visibility = ((engine != TranslationEngine.Azure) ? Visibility.Collapsed : Visibility.Visible);
	}

	private async void OnTestGoogleClick(object sender, RoutedEventArgs e)
	{
		BtnTestGoogle.IsEnabled = false;
		GoogleStatus.Text = "正在测试 Google 网页翻译...";
		GoogleStatus.Foreground = AppUi.Brush("UiMuted");
		try
		{
			Stopwatch sw = Stopwatch.StartNew();
			bool flag = await _providerManager.GoogleProvider.TestConnectionAsync();
			sw.Stop();
			if (flag)
			{
				GoogleStatus.Text = $"连接成功！测试翻译 \"Hello\" → 耗时 {sw.ElapsedMilliseconds}ms（非官方接口，可能限频）";
				GoogleStatus.Foreground = AppUi.Brush("UiSuccess");
			}
			else
			{
				GoogleStatus.Text = "连接失败：Google 非官方端点不可用（可能限频或被临时封禁），将自动切换备用引擎";
				GoogleStatus.Foreground = AppUi.Brush("UiError");
			}
		}
		catch (Exception ex)
		{
			GoogleStatus.Text = "测试异常: " + ex.Message;
			GoogleStatus.Foreground = AppUi.Brush("UiError");
		}
		finally
		{
			BtnTestGoogle.IsEnabled = true;
		}
	}

	private async void OnTestBingClick(object sender, RoutedEventArgs e)
	{
		BtnTestBing.IsEnabled = false;
		BingStatus.Text = "正在测试 Bing 网页翻译...";
		BingStatus.Foreground = AppUi.Brush("UiMuted");
		try
		{
			Stopwatch sw = Stopwatch.StartNew();
			bool flag = await _providerManager.BingProvider.TestConnectionAsync();
			sw.Stop();
			if (flag)
			{
				BingStatus.Text = $"连接成功！测试翻译 \"Hello\" → 耗时 {sw.ElapsedMilliseconds}ms（Edge 免费接口）";
				BingStatus.Foreground = AppUi.Brush("UiSuccess");
			}
			else
			{
				BingStatus.Text = "连接失败：Bing 免费翻译接口不可用（令牌端点可能变更），将自动切换备用引擎";
				BingStatus.Foreground = AppUi.Brush("UiError");
			}
		}
		catch (Exception ex)
		{
			BingStatus.Text = "测试异常: " + ex.Message;
			BingStatus.Foreground = AppUi.Brush("UiError");
		}
		finally
		{
			BtnTestBing.IsEnabled = true;
		}
	}

	private async void OnAutoFallbackChanged(object sender, RoutedEventArgs e)
	{
		if (_initialized)
		{
			_settingsManager.Settings.Translation.AutoFallback = CbAutoFallback.IsChecked == true;
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnGoogleEnabledChanged(object sender, RoutedEventArgs e)
	{
		if (_initialized)
		{
			bool valueOrDefault = CbGoogleEnabled.IsChecked == true;
			_settingsManager.Settings.Translation.GoogleEnabled = valueOrDefault;
			_providerManager.GoogleProvider.Configure(valueOrDefault);
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnBingEnabledChanged(object sender, RoutedEventArgs e)
	{
		if (_initialized)
		{
			bool valueOrDefault = CbBingEnabled.IsChecked == true;
			_settingsManager.Settings.Translation.BingEnabled = valueOrDefault;
			_providerManager.BingProvider.Configure(valueOrDefault);
			await _settingsManager.SaveAsync();
		}
	}

	private async void OnInstallEngineClick(object sender, RoutedEventArgs e)
	{
		BtnInstallEngine.IsEnabled = false;
		EngineProgress.Visibility = Visibility.Visible;
		EngineProgressText.Visibility = Visibility.Visible;
		try
		{
			_localInstaller.ProgressChanged += OnEngineProgress;
			await _localInstaller.InstallEngineAsync();
			_settingsManager.Settings.Translation.LocalEngineInstalled = true;
			await _settingsManager.SaveAsync();
			UpdateLocalEngineStatus();
			UpdateComponentStatus();
			_localProvider.NotifyConfigurationChanged();
			MessageBox.Show("本地翻译引擎安装完成！", "安装成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		catch (LocalEngineException ex)
		{
			ShowLocalEngineError("本地引擎安装失败", ex);
		}
		catch (Exception ex2)
		{
			MessageBox.Show("安装失败: " + ex2.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			_localInstaller.ProgressChanged -= OnEngineProgress;
			BtnInstallEngine.IsEnabled = true;
		}
	}

	private void OnEngineProgress(int percent, string message)
	{
		Dispatcher.Invoke(() =>
		{
			EngineProgress.Value = percent;
			EngineProgressText.Text = message;
		});
	}

	private async void OnRepairEngineClick(object sender, RoutedEventArgs e)
	{
		BtnRepairEngine.IsEnabled = false;
		BtnInstallEngine.IsEnabled = false;
		EngineProgress.Visibility = Visibility.Visible;
		EngineProgressText.Visibility = Visibility.Visible;
		try
		{
			_localInstaller.ProgressChanged += OnEngineProgress;
			EngineProgressText.Text = "正在检测并修复缺失组件（保留语言包与缓存）...";
			await _localInstaller.RepairAsync();
			_settingsManager.Settings.Translation.LocalEngineInstalled = _localInstaller.IsEngineReady;
			await _settingsManager.SaveAsync();
			UpdateLocalEngineStatus();
			UpdateComponentStatus();
			_localProvider.NotifyConfigurationChanged();
			List<LocalEngineErrorType> list = _localInstaller.VerifyComponents();
			if (list.Count == 0)
			{
				MessageBox.Show("本地翻译引擎修复完成！所有组件已恢复正常。\n已安装的语言包、词典和缓存均已保留。", "修复成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}
			MessageBox.Show($"修复完成，但仍有 {list.Count} 项组件异常：\n{string.Join("\n", list)}\n\n建议重新安装本地引擎。", "部分修复", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		catch (LocalEngineException ex)
		{
			ShowLocalEngineError("修复失败", ex);
		}
		catch (Exception ex2)
		{
			MessageBox.Show("修复失败: " + ex2.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			_localInstaller.ProgressChanged -= OnEngineProgress;
			BtnRepairEngine.IsEnabled = true;
			UpdateLocalEngineStatus();
		}
	}

	private void ShowLocalEngineError(string title, LocalEngineException ex)
	{
		string userFriendlyMessage = ex.GetUserFriendlyMessage();
		string logLine = ex.GetLogLine();
		StatusTranslation.Text = $"错误：{ex.ErrorType}";
		StatusTranslation.Foreground = AppUi.Brush("UiError");
		MessageBox.Show(userFriendlyMessage + "\n\n诊断信息：\n" + logLine, title, MessageBoxButton.OK, MessageBoxImage.Hand);
	}

	private void UpdateComponentStatus()
	{
		int count = _windowManager.BoundWindows.Count;
		StatusSteam.Text = ((count > 0) ? $"已绑定 {count} 个窗口" : "未绑定");
		StatusSteam.Foreground = new SolidColorBrush((count > 0) ? Color.FromRgb(39, 174, 96) : Color.FromRgb(136, 136, 136));
		if (_settingsManager.Settings.Translation.Engine != TranslationEngine.Local)
		{
			StatusEngine.Text = "当前引擎 · " + _providerManager.Name;
			StatusEngine.Foreground = _providerManager.IsConfigured ? AppUi.Brush("UiSuccess") : AppUi.Brush("UiWarning");
			StatusPackages.Text = "在线翻译无需本地语言包";
			StatusPackages.Foreground = AppUi.Brush("UiMuted");
			UpdateTranslationStatus();
			return;
		}
		EngineDiagnostics engineDiagnostics = _localInstaller.GetEngineDiagnostics();
		if (engineDiagnostics.ArgosImportable)
		{
			StatusEngine.Text = "已安装 v" + (engineDiagnostics.ArgosVersion ?? "?");
			StatusEngine.Foreground = AppUi.Brush("UiSuccess");
		}
		else if (engineDiagnostics.PythonInstalled)
		{
			StatusEngine.Text = (engineDiagnostics.PackagesDeployed ? "Python已装/组件待装" : "Python已装/组件待装");
			StatusEngine.Foreground = AppUi.Brush("UiWarning");
		}
		else
		{
			StatusEngine.Text = "未安装";
			StatusEngine.Foreground = AppUi.Brush("UiMuted");
		}
		List<string> installedLanguagePackages = _settingsManager.Settings.Translation.InstalledLanguagePackages;
		int num = ((engineDiagnostics.DeployedPackageCount > 0) ? engineDiagnostics.DeployedPackageCount : (installedLanguagePackages?.Count ?? 0));
		if (num > 0)
		{
			StatusPackages.Text = $"已部署 {num} 个";
			StatusPackages.Foreground = AppUi.Brush("UiSuccess");
		}
		else if (engineDiagnostics.LocalPackagesDownloaded)
		{
			StatusPackages.Text = $"已下载 {engineDiagnostics.LocalPackageCount} 个(待安装)";
			StatusPackages.Foreground = AppUi.Brush("UiWarning");
		}
		else
		{
			StatusPackages.Text = "未安装";
			StatusPackages.Foreground = AppUi.Brush("UiMuted");
		}
		UpdateTranslationStatus();
	}

	private bool WaitingForSteamForeground => _orchestrator.IsRunning &&
		_windowManager.PrimaryWindow is SteamWindowInfo window && !SteamWindowManager.IsSteamForeground(window);

	private void UpdateTranslationStatus()
	{
		if (WaitingForSteamForeground)
		{
			StatusTranslation.Text = "译文已隐藏 · 切回 Steam 恢复";
			StatusTranslation.Foreground = AppUi.Brush("UiWarning");
			return;
		}
		int displayed = _overlayRenderer.DisplayedTranslationCount;
		if (displayed > 0)
		{
			StatusTranslation.Text = $"正在显示 {displayed} 条译文";
			StatusTranslation.Foreground = AppUi.Brush("UiSuccess");
			return;
		}
		bool flag;
		string text;
		switch (_settingsManager.Settings.Translation.Engine)
		{
		case TranslationEngine.Google:
			flag = _settingsManager.Settings.Translation.GoogleEnabled;
			text = "Google引擎已禁用";
			break;
		case TranslationEngine.Bing:
			flag = _settingsManager.Settings.Translation.BingEnabled;
			text = "Bing引擎已禁用";
			break;
		case TranslationEngine.Local:
		{
			EngineDiagnostics engineDiagnostics = _localInstaller.GetEngineDiagnostics();
			flag = engineDiagnostics.ArgosImportable;
			text = (engineDiagnostics.PythonInstalled ? "翻译组件未装" : "本地引擎未安装");
			if (!flag)
			{
				break;
			}
			List<string> installedLanguagePackages = _settingsManager.Settings.Translation.InstalledLanguagePackages;
			int num;
			if (engineDiagnostics.DeployedPackageCount <= 0)
			{
				if (installedLanguagePackages == null)
				{
					goto IL_00cc;
				}
				num = installedLanguagePackages.Count;
			}
			else
			{
				num = engineDiagnostics.DeployedPackageCount;
			}
			if (num != 0)
			{
				break;
			}
			goto IL_00cc;
		}
		case TranslationEngine.LibreTranslate:
			flag = !string.IsNullOrEmpty(_settingsManager.Settings.Translation.LibreTranslateUrl);
			text = "LibreTranslate地址未配置";
			break;
		case TranslationEngine.Azure:
			flag = SecureStorage.HasApiKey;
			text = "Azure密钥未配置";
			break;
		default:
			{
				flag = false;
				text = "未知引擎";
				break;
			}
			IL_00cc:
			StatusTranslation.Text = "待机(语言包未装)";
			StatusTranslation.Foreground = AppUi.Brush("UiMuted");
			return;
		}
		if (!flag)
		{
			StatusTranslation.Text = "待机(" + text + ")";
			StatusTranslation.Foreground = AppUi.Brush("UiMuted");
		}
		else if (_lastTranslationError != null && (DateTime.Now - _lastErrorTime).TotalSeconds < 10.0)
		{
			StatusTranslation.Text = "错误: " + _lastTranslationError;
			StatusTranslation.Foreground = AppUi.Brush("UiError");
		}
		else if (_orchestrator.IsRunning && _orchestrator.Scheduler.IsProcessing)
		{
			StatusTranslation.Text = "正在翻译...";
			StatusTranslation.Foreground = AppUi.Brush("UiSuccess");
		}
		else if (_translationSuccessCount > 0 && (DateTime.Now - _lastTranslationTime).TotalSeconds < 30.0)
		{
			StatusTranslation.Text = $"已翻译 {_translationSuccessCount} 条";
			StatusTranslation.Foreground = AppUi.Brush("UiSuccess");
		}
		else if (_orchestrator.IsRunning)
		{
			if (_lastDiag != null && (DateTime.Now - _lastDiagTime).TotalMinutes < 5.0)
			{
				PipelineDiagnostics lastDiag = _lastDiag;
				if (lastDiag.FragmentCount == 0)
				{
					string value;
					if (!lastDiag.CaptureSuccess && lastDiag.OcrAttempted)
					{
						value = "截图失败";
					}
					else if (lastDiag.OcrAttempted && lastDiag.OcrCount == 0)
					{
						value = "OCR识别为空";
					}
					else
					{
						value = ((lastDiag.UiaCount == 0 && !lastDiag.OcrAttempted) ? "UIA无文本" : "无文本");
					}
					StatusTranslation.Text = $"等待文本({value} UIA={lastDiag.UiaCount} OCR={lastDiag.OcrCount})";
					StatusTranslation.Foreground = AppUi.Brush("UiWarning");
				}
				else if (lastDiag.SuccessCount > 0)
			{
				StatusTranslation.Text = "已有译文 · 正在核验可视位置";
				StatusTranslation.Foreground = AppUi.Brush("UiWarning");
			}
			else if (lastDiag.CandidateCount == 0)
				{
					StatusTranslation.Text = $"已采集{lastDiag.FragmentCount}条(无可翻译内容)";
					StatusTranslation.Foreground = AppUi.Brush("UiWarning");
				}
				else
				{
					StatusTranslation.Text = "就绪(等待文本)";
					StatusTranslation.Foreground = AppUi.Brush("UiSuccess");
				}
			}
			else
			{
				StatusTranslation.Text = "就绪(等待文本)";
				StatusTranslation.Foreground = AppUi.Brush("UiSuccess");
			}
		}
		else
		{
			StatusTranslation.Text = "待机";
			StatusTranslation.Foreground = AppUi.Brush("UiMuted");
		}
	}

	private void OnTranslationCompleted(object? sender, TranslationResult result)
	{
		if (result.Success)
		{
			Interlocked.Increment(ref _translationSuccessCount);
		}
		else
		{
			Interlocked.Increment(ref _translationFailCount);
			_lastTranslationError = result.ErrorMessage;
			_lastErrorTime = DateTime.Now;
		}
		_lastTranslationTime = DateTime.Now;
	}

	private void OnPipelineDiagnostics(object? sender, PipelineDiagnostics diag)
	{
		_lastTranslateMs = diag.TranslateMs;
		_lastDiag = diag;
		_lastDiagTime = DateTime.Now;
		Dispatcher.BeginInvoke((Action)(() =>
		{
			UpdateTranslationStatus();
			UpdateDiagnosticsPanel();
		}));
	}

	private void OnToggleDiagClick(object sender, RoutedEventArgs e)
	{
		if (DiagPanel.Visibility == Visibility.Collapsed)
		{
			DiagPanel.Visibility = Visibility.Visible;
			BtnToggleDiag.Content = "折叠";
			UpdateDiagnosticsPanel();
		}
		else
		{
			DiagPanel.Visibility = Visibility.Collapsed;
			BtnToggleDiag.Content = "展开";
		}
	}

	private void UpdateDiagnosticsPanel()
	{
		PipelineDiagnostics lastDiag = _lastDiag;
		if (lastDiag == null || (DateTime.Now - _lastDiagTime).TotalMinutes > 5.0)
		{
			DiagCapture.Text = "未执行";
			DiagCapture.Foreground = AppUi.Brush("UiMuted");
			DiagUia.Text = "未执行";
			DiagUia.Foreground = AppUi.Brush("UiMuted");
			DiagOcr.Text = "未执行";
			DiagOcr.Foreground = AppUi.Brush("UiMuted");
			DiagLang.Text = "未执行";
			DiagLang.Foreground = AppUi.Brush("UiMuted");
			DiagQueue.Text = _orchestrator.Scheduler.QueueDepth.ToString();
			DiagEngine.Text = "未执行";
			DiagEngine.Foreground = AppUi.Brush("UiMuted");
			DiagResults.Text = "0条";
			DiagOverlay.Text = WaitingForSteamForeground ? "已隐藏（Steam 在后台）" : _overlayRenderer.DisplayedTranslationCount + "条";
			DiagDetail.Text = (_orchestrator.IsRunning ? "等待首次管线执行..." : "翻译服务未启动");
			UpdateScanStatusPanel();
			return;
		}
		TextBlock diagCapture = DiagCapture;
		string text;
		if (lastDiag.CaptureSuccess)
		{
			text = "成功";
		}
		else
		{
			text = (lastDiag.OcrAttempted ? "失败" : "未尝试");
		}
		diagCapture.Text = text;
		DiagCapture.Foreground = new SolidColorBrush(lastDiag.CaptureSuccess ? Color.FromRgb(39, 174, 96) : Color.FromRgb(231, 76, 60));
		DiagUia.Text = $"{lastDiag.UiaCount}条";
		DiagUia.Foreground = new SolidColorBrush((lastDiag.UiaCount > 0) ? Color.FromRgb(39, 174, 96) : Color.FromRgb(230, 115, 0));
		DiagOcr.Text = (lastDiag.OcrAttempted ? ($"{lastDiag.OcrCount}条" + ((lastDiag.OcrCount == 0) ? "(识别为空)" : "")) : "未尝试(UIA已够)");
		TextBlock diagOcr = DiagOcr;
		Color color;
		if (lastDiag.OcrCount > 0)
		{
			color = Color.FromRgb(39, 174, 96);
		}
		else
		{
			color = (lastDiag.OcrAttempted ? Color.FromRgb(231, 76, 60) : Color.FromRgb(136, 136, 136));
		}
		diagOcr.Foreground = new SolidColorBrush(color);
		DiagLang.Text = (string.IsNullOrEmpty(lastDiag.DetectedLanguages) ? "无" : lastDiag.DetectedLanguages);
		TextBlock diagLang = DiagLang;
		string? detectedLanguages = lastDiag.DetectedLanguages;
		diagLang.Foreground = new SolidColorBrush((detectedLanguages != null && detectedLanguages.Contains("en")) ? Color.FromRgb(39, 174, 96) : Color.FromRgb(136, 136, 136));
		DiagQueue.Text = _orchestrator.Scheduler.QueueDepth.ToString();
		if (lastDiag.SuccessCount > 0)
		{
			DiagEngine.Text = $"成功({lastDiag.TranslateMs}ms)";
			DiagEngine.Foreground = AppUi.Brush("UiSuccess");
		}
		else if (lastDiag.FailCount > 0)
		{
			DiagEngine.Text = $"失败{lastDiag.FailCount}条";
			DiagEngine.Foreground = AppUi.Brush("UiError");
		}
		else if (lastDiag.CandidateCount > 0)
		{
			DiagEngine.Text = "执行中...";
			DiagEngine.Foreground = AppUi.Brush("UiWarning");
		}
		else
		{
			DiagEngine.Text = "无候选文本";
			DiagEngine.Foreground = AppUi.Brush("UiMuted");
		}
		DiagResults.Text = $"{lastDiag.SuccessCount}条";
		DiagResults.Foreground = new SolidColorBrush((lastDiag.SuccessCount > 0) ? Color.FromRgb(39, 174, 96) : Color.FromRgb(136, 136, 136));
		int num = (_overlayRenderer as OverlayRenderer)?.DisplayedTranslationCount ?? 0;
		DiagOverlay.Text = WaitingForSteamForeground ? "已隐藏（Steam 在后台）" : $"{num}条";
		DiagOverlay.Foreground = new SolidColorBrush((num > 0) ? Color.FromRgb(39, 174, 96) : Color.FromRgb(230, 115, 0));
		List<string> list = new List<string>();
		if (!string.IsNullOrEmpty(lastDiag.PageArea))
		{
			list.Add("页面:" + lastDiag.PageArea);
		}
		if (lastDiag.FragmentCount > 0)
		{
			list.Add($"采集:{lastDiag.FragmentCount}条/{lastDiag.AcquireMs}ms");
		}
		if (lastDiag.RegionAdded > 0 || lastDiag.RegionMoved > 0 || lastDiag.RegionRemoved > 0)
		{
			list.Add($"区域:+{lastDiag.RegionAdded}/~{lastDiag.RegionMoved}/-{lastDiag.RegionRemoved}");
		}
		if (!string.IsNullOrEmpty(lastDiag.RegionTypes))
		{
			list.Add("类型:" + lastDiag.RegionTypes);
		}
		if (lastDiag.CandidateCount > 0)
		{
			list.Add($"候选:{lastDiag.CandidateCount}");
		}
		if (lastDiag.CacheHits > 0)
		{
			list.Add($"缓存命中:{lastDiag.CacheHits}");
		}
		if (!string.IsNullOrEmpty(lastDiag.FilterReason))
		{
			list.Add("⚠" + lastDiag.FilterReason);
		}
		if (!string.IsNullOrEmpty(lastDiag.LastError))
		{
			list.Add("错误:" + lastDiag.LastError);
		}
		DiagDetail.Text = (WaitingForSteamForeground ? "上次扫描 · " : "") + string.Join(" | ", list);
		UpdateScanStatusPanel();
	}

	private void UpdateScanStatusPanel()
	{
		TranslationOrchestrator orchestrator = _orchestrator;
		if (orchestrator != null)
		{
			DiagScanStatus.Text = orchestrator.ScanStatus;
			TextBlock diagScanStatus = DiagScanStatus;
			Color color;
			if (orchestrator.ScanStatus.Contains("运行"))
			{
				color = Color.FromRgb(39, 174, 96);
			}
			else if (orchestrator.ScanStatus.Contains("暂停"))
			{
				color = Color.FromRgb(230, 115, 0);
			}
			else
			{
				color = ((orchestrator.ScanStatus.Contains("异常") || orchestrator.ScanStatus.Contains("失败")) ? Color.FromRgb(231, 76, 60) : Color.FromRgb(136, 136, 136));
			}
			diagScanStatus.Foreground = new SolidColorBrush(color);
			DiagLastScan.Text = ((orchestrator.LastScanTime == DateTime.MinValue) ? "-" : (orchestrator.LastScanTime.ToString("HH:mm:ss") + $"({(DateTime.Now - orchestrator.LastScanTime).TotalSeconds:F0}秒前)"));
			DiagLastCapture.Text = ((orchestrator.LastCaptureTime == DateTime.MinValue) ? "-" : (orchestrator.LastCaptureTime.ToString("HH:mm:ss") + $"({(DateTime.Now - orchestrator.LastCaptureTime).TotalSeconds:F0}秒前)"));
			DiagRecovery.Text = ((orchestrator.AutoRecoveryCount > 0) ? $"{orchestrator.AutoRecoveryCount}次" : "0次");
			DiagRecovery.Foreground = new SolidColorBrush((orchestrator.AutoRecoveryCount > 0) ? Color.FromRgb(230, 115, 0) : Color.FromRgb(136, 136, 136));
		}
	}

	private async void OnInstallPackageClick(object sender, RoutedEventArgs e)
	{
		if (!(sender is Button { Tag: var tag } btn) || !(tag is string fromCode))
		{
			return;
		}
		btn.IsEnabled = false;
		PackageProgressText.Visibility = Visibility.Visible;
		try
		{
			SetPackageProgress("步骤 1/8：检测本地引擎...");
			if (!_localInstaller.IsEngineReady)
			{
				MessageBox.Show("本地翻译引擎未就绪，请先点击「安装本地引擎」。", "无法安装语言包", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			SetPackageProgress("步骤 2/8：校验桥接脚本...");
			if (!_localInstaller.IsBridgeScriptValid)
			{
				throw new LocalEngineException("翻译桥接脚本缺失或损坏", LocalEngineErrorType.BridgeScriptMissing);
			}
			SetPackageProgress("步骤 3/8：连接翻译桥接...");
			PythonBridge bridge = await _localProvider.GetBridgeForManagementAsync();
			SetPackageProgress("步骤 4/8：下载并安装语言包 " + fromCode + "→zh（可能需要数分钟）...");
			PackageInstallResult result = await _localInstaller.InstallLanguagePackageAsync(bridge, fromCode, "zh");
			SetPackageProgress("步骤 5/8：验证安装结果...");
			if (!result.Success)
			{
				LocalEngineException ex = new LocalEngineException(result.ErrorMessage, result.ErrorType);
				ShowLocalEngineError("语言包 " + fromCode + "→zh 安装失败", ex);
				return;
			}
			SetPackageProgress("步骤 6/8：测试翻译（验证 " + fromCode + "→zh 可用）...");
			string testText;
			if (fromCode == "ja")
			{
				testText = "こんにちは";
			}
			else
			{
				testText = ((fromCode == "ko") ? "안녕하세요" : "Hello, world!");
			}
			TranslationResult testResult = await _localProvider.TranslateAsync(testText, fromCode, "zh");
			if (!testResult.Success || string.IsNullOrWhiteSpace(testResult.TranslatedText))
			{
				LocalEngineException ex2 = new LocalEngineException("翻译测试失败：" + (testResult.ErrorMessage ?? "译文为空"), LocalEngineErrorType.TranslationTestFailed);
				ShowLocalEngineError("语言包已下载但翻译测试未通过", ex2);
				return;
			}
			SetPackageProgress("步骤 7/8：更新语言包列表...");
			List<string> packages = await _localInstaller.GetInstalledPackagesAsync(bridge);
			_settingsManager.Settings.Translation.InstalledLanguagePackages = packages;
			await _settingsManager.SaveAsync();
			UpdateInstalledPackagesDisplay(packages);
			_localProvider.NotifyConfigurationChanged();
			SetPackageProgress("步骤 8/8：完成");
			UpdateComponentStatus();
			string text = $"语言包 {fromCode}→zh 安装完成！\n\n测试翻译：{testText} → {testResult.TranslatedText}";
			if (result.IsPivot)
			{
				text = text + "\n\n注意：" + result.PivotNote;
			}
			MessageBox.Show(text, "安装成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		catch (LocalEngineException ex3)
		{
			ShowLocalEngineError("语言包 " + fromCode + "→zh 安装失败", ex3);
		}
		catch (Exception ex4)
		{
			MessageBox.Show("安装失败: " + ex4.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			btn.IsEnabled = true;
			PackageProgressText.Visibility = Visibility.Collapsed;
		}
	}

	private void SetPackageProgress(string text)
	{
		Dispatcher.Invoke(() => PackageProgressText.Text = text);
	}

	private async void OnSaveLibreClick(object sender, RoutedEventArgs e)
	{
		string text = TxtLibreUrl.Text.Trim();
		string text2 = TxtLibreKey.Password.Trim();
		if (string.IsNullOrEmpty(text))
		{
			LibreStatus.Text = "请输入服务地址";
			LibreStatus.Foreground = AppUi.Brush("UiError");
			return;
		}
		BtnSaveLibre.IsEnabled = false;
		LibreStatus.Text = "正在测试连接...";
		LibreStatus.Foreground = AppUi.Brush("UiMuted");
		try
		{
			LibreTranslateProvider libreProvider = _services.GetRequiredService<LibreTranslateProvider>();
			libreProvider.Configure(text, text2);
			_settingsManager.Settings.Translation.LibreTranslateUrl = text;
			_settingsManager.Settings.Translation.LibreTranslateApiKey = text2;
			await _settingsManager.SaveAsync();
			if (await libreProvider.TestConnectionAsync())
			{
				LibreStatus.Text = "连接测试成功，翻译服务可用";
				LibreStatus.Foreground = AppUi.Brush("UiSuccess");
			}
			else
			{
				LibreStatus.Text = "连接测试失败，请检查服务地址";
				LibreStatus.Foreground = AppUi.Brush("UiError");
			}
		}
		catch (Exception ex)
		{
			LibreStatus.Text = "错误: " + ex.Message;
			LibreStatus.Foreground = AppUi.Brush("UiError");
		}
		finally
		{
			BtnSaveLibre.IsEnabled = true;
		}
	}

	private async Task StartOrchestratorAsync()
	{
		BtnToggle.IsEnabled = false;
		try
		{
			await SaveSettingsFromUiAsync();
			await _orchestrator.StartAsync();
			if (_windowManager.PrimaryWindow is SteamWindowInfo window &&
				SteamWindowManager.TryActivateForRescan(window, new WindowInteropHelper(this).Handle))
				_orchestrator.ForceRescanWindow(window);
		}
		catch (Exception ex)
		{
			MessageBox.Show("启动失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			BtnToggle.IsEnabled = true;
		}
	}

	private async void OnSaveKeyClick(object sender, RoutedEventArgs e)
	{
		string apiKey = TxtApiKey.Password.Trim();
		string region = TxtRegion.Text.Trim();
		if (string.IsNullOrEmpty(apiKey))
		{
			KeyStatus.Text = "请输入 API 密钥";
			KeyStatus.Foreground = AppUi.Brush("UiError");
			return;
		}
		BtnSaveKey.IsEnabled = false;
		KeyStatus.Text = "正在保存并测试连接...";
		KeyStatus.Foreground = AppUi.Brush("UiMuted");
		try
		{
			SecureStorage.SaveApiKey(apiKey);
			_settingsManager.Settings.Translation.AzureRegion = region;
			await _settingsManager.SaveAsync();
			AzureTranslatorProvider requiredService = _services.GetRequiredService<AzureTranslatorProvider>();
			requiredService.Configure(apiKey, region);
			if (await requiredService.TestConnectionAsync())
			{
				KeyStatus.Text = "连接测试成功，翻译服务可用";
				KeyStatus.Foreground = AppUi.Brush("UiSuccess");
				TxtApiKey.Clear();
			}
			else
			{
				KeyStatus.Text = "连接测试失败，请检查密钥和区域";
				KeyStatus.Foreground = AppUi.Brush("UiError");
			}
		}
		catch (Exception ex)
		{
			KeyStatus.Text = "错误: " + ex.Message;
			KeyStatus.Foreground = AppUi.Brush("UiError");
		}
		finally
		{
			BtnSaveKey.IsEnabled = true;
		}
	}

	private async void OnRescanClick(object sender, RoutedEventArgs e)
	{
		BtnRescan.IsEnabled = false;
		try
		{
			await _windowManager.ScanAndBindAsync();
			UpdateBoundWindowsDisplay();
			SteamWindowInfo? window = _windowManager.PrimaryWindow;
			if (window == null)
			{
				DetailStatus.Text = "未检测到 Steam 窗口，请确认 Steam 正在运行";
				return;
			}
			if (!_orchestrator.IsRunning)
			{
				DetailStatus.Text = "已绑定 Steam，启动翻译后继续扫描";
				return;
			}
			if (SteamWindowManager.TryActivateForRescan(window, new WindowInteropHelper(this).Handle))
			{
				_orchestrator.ForceRescanWindow(window);
				DetailStatus.Text = "已返回 Steam，正在核验当前页面";
			}
			else
			{
				DetailStatus.Text = "已绑定 Steam，切回 Steam 后继续扫描";
			}
		}
		finally
		{
			BtnRescan.IsEnabled = true;
		}
	}

	private async void OnExportDiagClick(object sender, RoutedEventArgs e)
	{
		try
		{
			string contents = _compatibilityManager.ExportDiagnostics();
			SaveFileDialog dialog = new SaveFileDialog
			{
				Filter = "JSON 文件|*.json",
				FileName = $"SteamCN_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.json"
			};
			if (dialog.ShowDialog() == true)
			{
				await File.WriteAllTextAsync(dialog.FileName, contents);
				MessageBox.Show("诊断信息已导出到:\n" + dialog.FileName, "导出成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("导出失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void OnExitClick(object sender, RoutedEventArgs e)
	{
		if (Application.Current is App app)
		{
			app.ExitApplication();
		}
	}

	private void OnWindowClosing(object sender, CancelEventArgs e)
	{
		e.Cancel = true;
		Hide();
	}

	private void OnRunningStateChanged(bool isRunning)
	{
		Dispatcher.Invoke(() =>
		{
			UpdateRunningState(isRunning);
		});
	}

	private void OnStatusMessage(string message)
	{
		Dispatcher.Invoke(() => DetailStatus.Text = message);
	}

	private void OnErrorOccurred(string error)
	{
		Dispatcher.Invoke(() =>
		{
			DetailStatus.Text = error;
			StatusDot.Fill = AppUi.Brush("UiError");
		});
	}

	private void OnWindowBound(object? sender, SteamWindowInfo window)
	{
		Dispatcher.Invoke(() =>
		{
			UpdateBoundWindowsDisplay();
			UpdateComponentStatus();
		});
	}

	private void OnWindowUnbound(object? sender, SteamWindowInfo window)
	{
		Dispatcher.Invoke(() =>
		{
			UpdateBoundWindowsDisplay();
			UpdateComponentStatus();
		});
	}

	private void UpdateRunningState(bool isRunning)
	{
		if (isRunning)
		{
			StatusDot.Fill = AppUi.Brush("UiSuccess");
			StatusText.Text = "运行中";
			BtnToggle.Content = "停止翻译";
		}
		else
		{
			StatusDot.Fill = new SolidColorBrush(Color.FromRgb(204, 204, 204));
			StatusText.Text = "未启动";
			BtnToggle.Content = "启动翻译";
		}
		UpdateComponentStatus();
	}

	private void UpdateBoundWindowsDisplay()
	{
		IReadOnlyList<SteamWindowInfo> boundWindows = _windowManager.BoundWindows;
		if (boundWindows.Count == 0)
		{
			BoundWindowText.Text = "无绑定窗口";
			return;
		}
		BoundWindowText.Text = string.Join("\n", boundWindows.Select((SteamWindowInfo w) => $"• {w.Title} (PID: {w.ProcessId})"));
	}

	private void UpdateStatistics()
	{
		StatRequests.Text = _providerManager.TotalRequests.ToString("N0");
		StatCharacters.Text = _providerManager.TotalCharactersSubmitted.ToString("N0");
		StatCache.Text = _translationMemory.Count.ToString("N0");
		StatSuccess.Text = _translationSuccessCount.ToString("N0");
		StatFail.Text = _translationFailCount.ToString("N0");
		StatAvgMs.Text = ((_lastTranslateMs >= 0) ? $"{_lastTranslateMs}ms" : "-");
		UpdateTranslationStatus();
		if (DiagPanel.Visibility == Visibility.Visible)
		{
			UpdateDiagnosticsPanel();
		}
	}

	private void UpdateLocalEngineStatus()
	{
		EngineDiagnostics engineDiagnostics = _localInstaller.GetEngineDiagnostics();
		if (engineDiagnostics.ArgosImportable)
		{
			LocalEngineStatus.Text = "状态：已安装 v" + (engineDiagnostics.ArgosVersion ?? "?") + "，可离线翻译";
			LocalEngineStatus.Foreground = AppUi.Brush("UiSuccess");
			BtnInstallEngine.Content = "引擎已安装";
			BtnInstallEngine.IsEnabled = false;
			RefreshInstalledPackagesAsync();
		}
		else if (engineDiagnostics.PythonInstalled)
		{
			LocalEngineStatus.Text = (engineDiagnostics.PackagesDeployed ? "状态：Python已装，语言包已部署，翻译组件待安装" : "状态：Python 已安装，翻译组件待安装");
			LocalEngineStatus.Foreground = AppUi.Brush("UiWarning");
			BtnInstallEngine.Content = "安装本地引擎";
			BtnInstallEngine.IsEnabled = true;
		}
		else
		{
			LocalEngineStatus.Text = "状态：未安装（点击下方按钮安装，约 100MB）";
			LocalEngineStatus.Foreground = AppUi.Brush("UiMuted");
			BtnInstallEngine.Content = "安装本地引擎";
			BtnInstallEngine.IsEnabled = true;
		}
	}

	private async Task RefreshInstalledPackagesAsync()
	{
		_ = 1;
		try
		{
			if (_localInstaller.IsEngineReady)
			{
				PythonBridge bridge = await _localProvider.GetBridgeForManagementAsync();
				List<string> list = await _localInstaller.GetInstalledPackagesAsync(bridge);
				_settingsManager.Settings.Translation.InstalledLanguagePackages = list;
				UpdateInstalledPackagesDisplay(list);
			}
		}
		catch
		{
		}
	}

	private void UpdateInstalledPackagesDisplay(List<string> packages)
	{
		InstalledPackagesText.Text = ((packages.Count > 0) ? ("已安装：" + string.Join("、", packages)) : "已安装：无（请安装至少一个语言包）");
		UpdateComponentStatus();
	}

	private void InitializeComponent()
	{
		Title = "SteamCN Translator - Steam 外挂翻译器";
		Width = 844;
		MinWidth = 630;
		MinHeight = 500;
		Height = Math.Clamp(SystemParameters.WorkArea.Height - 28, MinHeight, 758);
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		FontFamily = new FontFamily("Inter, Segoe UI, Microsoft YaHei UI");
		FontSize = 15;
		Foreground = AppUi.Brush("UiInk");
		var view = AppUi.Load<Grid>("MainWindow");
		Content = view;

		StatusDot = AppUi.Require<Rectangle>(view, nameof(StatusDot));
		StatusText = AppUi.Require<TextBlock>(view, nameof(StatusText));
		DetailStatus = AppUi.Require<TextBlock>(view, nameof(DetailStatus));
		BtnToggle = AppUi.Require<Button>(view, nameof(BtnToggle));
		BoundWindowText = AppUi.Require<TextBlock>(view, nameof(BoundWindowText));
		BtnRescan = AppUi.Require<Button>(view, nameof(BtnRescan));
		StatusSteam = AppUi.Require<TextBlock>(view, nameof(StatusSteam));
		StatusEngine = AppUi.Require<TextBlock>(view, nameof(StatusEngine));
		StatusPackages = AppUi.Require<TextBlock>(view, nameof(StatusPackages));
		StatusTranslation = AppUi.Require<TextBlock>(view, nameof(StatusTranslation));
		BtnToggleDiag = AppUi.Require<Button>(view, nameof(BtnToggleDiag));
		DiagPanel = AppUi.Require<StackPanel>(view, nameof(DiagPanel));
		DiagCapture = AppUi.Require<TextBlock>(view, nameof(DiagCapture));
		DiagUia = AppUi.Require<TextBlock>(view, nameof(DiagUia));
		DiagOcr = AppUi.Require<TextBlock>(view, nameof(DiagOcr));
		DiagLang = AppUi.Require<TextBlock>(view, nameof(DiagLang));
		DiagQueue = AppUi.Require<TextBlock>(view, nameof(DiagQueue));
		DiagEngine = AppUi.Require<TextBlock>(view, nameof(DiagEngine));
		DiagResults = AppUi.Require<TextBlock>(view, nameof(DiagResults));
		DiagOverlay = AppUi.Require<TextBlock>(view, nameof(DiagOverlay));
		DiagScanStatus = AppUi.Require<TextBlock>(view, nameof(DiagScanStatus));
		DiagLastScan = AppUi.Require<TextBlock>(view, nameof(DiagLastScan));
		DiagLastCapture = AppUi.Require<TextBlock>(view, nameof(DiagLastCapture));
		DiagRecovery = AppUi.Require<TextBlock>(view, nameof(DiagRecovery));
		DiagDetail = AppUi.Require<TextBlock>(view, nameof(DiagDetail));
		RbEngineGoogle = AppUi.Require<RadioButton>(view, nameof(RbEngineGoogle));
		RbEngineBing = AppUi.Require<RadioButton>(view, nameof(RbEngineBing));
		RbEngineLocal = AppUi.Require<RadioButton>(view, nameof(RbEngineLocal));
		RbEngineLibre = AppUi.Require<RadioButton>(view, nameof(RbEngineLibre));
		RbEngineAzure = AppUi.Require<RadioButton>(view, nameof(RbEngineAzure));
		CbAutoFallback = AppUi.Require<CheckBox>(view, nameof(CbAutoFallback));
		PanelGoogle = AppUi.Require<Border>(view, nameof(PanelGoogle));
		CbGoogleEnabled = AppUi.Require<CheckBox>(view, nameof(CbGoogleEnabled));
		BtnTestGoogle = AppUi.Require<Button>(view, nameof(BtnTestGoogle));
		GoogleStatus = AppUi.Require<TextBlock>(view, nameof(GoogleStatus));
		PanelBing = AppUi.Require<Border>(view, nameof(PanelBing));
		CbBingEnabled = AppUi.Require<CheckBox>(view, nameof(CbBingEnabled));
		BtnTestBing = AppUi.Require<Button>(view, nameof(BtnTestBing));
		BingStatus = AppUi.Require<TextBlock>(view, nameof(BingStatus));
		PanelLocal = AppUi.Require<Border>(view, nameof(PanelLocal));
		LocalEngineStatus = AppUi.Require<TextBlock>(view, nameof(LocalEngineStatus));
		BtnInstallEngine = AppUi.Require<Button>(view, nameof(BtnInstallEngine));
		BtnRepairEngine = AppUi.Require<Button>(view, nameof(BtnRepairEngine));
		EngineProgress = AppUi.Require<ProgressBar>(view, nameof(EngineProgress));
		EngineProgressText = AppUi.Require<TextBlock>(view, nameof(EngineProgressText));
		InstalledPackagesText = AppUi.Require<TextBlock>(view, nameof(InstalledPackagesText));
		BtnInstallEnZh = AppUi.Require<Button>(view, nameof(BtnInstallEnZh));
		BtnInstallJaZh = AppUi.Require<Button>(view, nameof(BtnInstallJaZh));
		BtnInstallKoZh = AppUi.Require<Button>(view, nameof(BtnInstallKoZh));
		PackageProgressText = AppUi.Require<TextBlock>(view, nameof(PackageProgressText));
		PanelLibre = AppUi.Require<Border>(view, nameof(PanelLibre));
		TxtLibreUrl = AppUi.Require<TextBox>(view, nameof(TxtLibreUrl));
		TxtLibreKey = AppUi.Require<PasswordBox>(view, nameof(TxtLibreKey));
		BtnSaveLibre = AppUi.Require<Button>(view, nameof(BtnSaveLibre));
		LibreStatus = AppUi.Require<TextBlock>(view, nameof(LibreStatus));
		PanelAzure = AppUi.Require<Border>(view, nameof(PanelAzure));
		TxtApiKey = AppUi.Require<PasswordBox>(view, nameof(TxtApiKey));
		TxtRegion = AppUi.Require<TextBox>(view, nameof(TxtRegion));
		BtnSaveKey = AppUi.Require<Button>(view, nameof(BtnSaveKey));
		KeyStatus = AppUi.Require<TextBlock>(view, nameof(KeyStatus));
		RbOriginal = AppUi.Require<RadioButton>(view, nameof(RbOriginal));
		RbBilingual = AppUi.Require<RadioButton>(view, nameof(RbBilingual));
		RbChinese = AppUi.Require<RadioButton>(view, nameof(RbChinese));
		RbLayoutSide = AppUi.Require<RadioButton>(view, nameof(RbLayoutSide));
		RbLayoutOverlay = AppUi.Require<RadioButton>(view, nameof(RbLayoutOverlay));
		RbLayoutAdaptive = AppUi.Require<RadioButton>(view, nameof(RbLayoutAdaptive));
		SliderOverlayOpacity = AppUi.Require<Slider>(view, nameof(SliderOverlayOpacity));
		LblOverlayOpacity = AppUi.Require<TextBlock>(view, nameof(LblOverlayOpacity));
		SliderFontScale = AppUi.Require<Slider>(view, nameof(SliderFontScale));
		LblFontScale = AppUi.Require<TextBlock>(view, nameof(LblFontScale));
		SliderMaxWidth = AppUi.Require<Slider>(view, nameof(SliderMaxWidth));
		LblMaxWidth = AppUi.Require<TextBlock>(view, nameof(LblMaxWidth));
		SliderBilingualGap = AppUi.Require<Slider>(view, nameof(SliderBilingualGap));
		LblBilingualGap = AppUi.Require<TextBlock>(view, nameof(LblBilingualGap));
		CbShowOriginal = AppUi.Require<CheckBox>(view, nameof(CbShowOriginal));
		CbWorkshop = AppUi.Require<CheckBox>(view, nameof(CbWorkshop));
		CbReviews = AppUi.Require<CheckBox>(view, nameof(CbReviews));
		CbCommunity = AppUi.Require<CheckBox>(view, nameof(CbCommunity));
		CbStore = AppUi.Require<CheckBox>(view, nameof(CbStore));
		CbGuides = AppUi.Require<CheckBox>(view, nameof(CbGuides));
		StatRequests = AppUi.Require<TextBlock>(view, nameof(StatRequests));
		StatCharacters = AppUi.Require<TextBlock>(view, nameof(StatCharacters));
		StatCache = AppUi.Require<TextBlock>(view, nameof(StatCache));
		StatSuccess = AppUi.Require<TextBlock>(view, nameof(StatSuccess));
		StatFail = AppUi.Require<TextBlock>(view, nameof(StatFail));
		StatAvgMs = AppUi.Require<TextBlock>(view, nameof(StatAvgMs));
		BtnExportDiag = AppUi.Require<Button>(view, nameof(BtnExportDiag));
		BtnExit = AppUi.Require<Button>(view, nameof(BtnExit));

		Closing += OnWindowClosing;
		BtnToggle.Click += OnToggleClick;
		BtnRescan.Click += OnRescanClick;
		BtnToggleDiag.Click += OnToggleDiagClick;
		RbEngineGoogle.Checked += OnEngineChanged;
		RbEngineBing.Checked += OnEngineChanged;
		RbEngineLocal.Checked += OnEngineChanged;
		RbEngineLibre.Checked += OnEngineChanged;
		RbEngineAzure.Checked += OnEngineChanged;
		CbAutoFallback.Checked += OnAutoFallbackChanged;
		CbAutoFallback.Unchecked += OnAutoFallbackChanged;
		CbGoogleEnabled.Checked += OnGoogleEnabledChanged;
		CbGoogleEnabled.Unchecked += OnGoogleEnabledChanged;
		BtnTestGoogle.Click += OnTestGoogleClick;
		CbBingEnabled.Checked += OnBingEnabledChanged;
		CbBingEnabled.Unchecked += OnBingEnabledChanged;
		BtnTestBing.Click += OnTestBingClick;
		BtnInstallEngine.Click += OnInstallEngineClick;
		BtnRepairEngine.Click += OnRepairEngineClick;
		BtnInstallEnZh.Click += OnInstallPackageClick;
		BtnInstallJaZh.Click += OnInstallPackageClick;
		BtnInstallKoZh.Click += OnInstallPackageClick;
		BtnSaveLibre.Click += OnSaveLibreClick;
		BtnSaveKey.Click += OnSaveKeyClick;
		RbOriginal.Checked += OnDisplayModeChanged;
		RbBilingual.Checked += OnDisplayModeChanged;
		RbChinese.Checked += OnDisplayModeChanged;
		RbLayoutSide.Checked += OnLayoutModeChanged;
		RbLayoutOverlay.Checked += OnLayoutModeChanged;
		RbLayoutAdaptive.Checked += OnLayoutModeChanged;
		SliderOverlayOpacity.ValueChanged += OnOverlayOpacityChanged;
		SliderFontScale.ValueChanged += OnFontScaleChanged;
		SliderMaxWidth.ValueChanged += OnMaxWidthChanged;
		SliderBilingualGap.ValueChanged += OnBilingualGapChanged;
		CbShowOriginal.Checked += OnShowOriginalChanged;
		CbShowOriginal.Unchecked += OnShowOriginalChanged;
		CbWorkshop.Checked += OnAreaToggled;
		CbWorkshop.Unchecked += OnAreaToggled;
		CbReviews.Checked += OnAreaToggled;
		CbReviews.Unchecked += OnAreaToggled;
		CbCommunity.Checked += OnAreaToggled;
		CbCommunity.Unchecked += OnAreaToggled;
		CbStore.Checked += OnAreaToggled;
		CbStore.Unchecked += OnAreaToggled;
		CbGuides.Checked += OnAreaToggled;
		CbGuides.Unchecked += OnAreaToggled;
		BtnExportDiag.Click += OnExportDiagClick;
		BtnExit.Click += OnExitClick;
	}
}
