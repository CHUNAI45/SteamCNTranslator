using System;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using SteamCN.Compatibility;
using SteamCN.Core.Interfaces;
using SteamCN.Core.Pipeline;
using SteamCN.Ocr;
using SteamCN.Storage;
using SteamCN.Translation;
using SteamCN.Windows;

namespace SteamCN.App.Services;

public static class ServiceConfigurator
{
	public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamCNTranslator");

	public static IServiceProvider Build()
	{
		ServiceCollection services = new ServiceCollection();
		Directory.CreateDirectory(DataDirectory);
		string dbPath = Path.Combine(DataDirectory, "translation_memory.db");
		((IServiceCollection)services).AddSingleton((Func<IServiceProvider, ISettingsManager>)((IServiceProvider sp) => new SettingsManager(DataDirectory)));
		services.AddSingleton((IServiceProvider sp) => new DatabaseManager(dbPath));
		services.AddSingleton<ITranslationMemory, TranslationMemoryService>();
		services.AddSingleton<IGlossaryProcessor, GlossaryService>();
		services.AddSingleton<IOcrEngine, WindowsOcrEngine>();
		services.AddSingleton<ISteamWindowManager, SteamWindowManager>();
		services.AddSingleton<ITextAcquisition, HybridTextAcquisition>();
		services.AddSingleton<ITextRegionTracker, TextRegionTracker>();
		services.AddSingleton<IOverlayRenderer, OverlayRenderer>();
		services.AddSingleton((IServiceProvider sp) => new LocalEngineInstaller(new HttpClient()));
		services.AddSingleton<GoogleTranslateProvider>();
		services.AddSingleton<BingTranslateProvider>();
		services.AddSingleton<LocalTranslatorProvider>();
		services.AddSingleton<LibreTranslateProvider>();
		services.AddSingleton<AzureTranslatorProvider>();
		services.AddSingleton<TranslationProviderManager>();
		((IServiceCollection)services).AddSingleton((Func<IServiceProvider, ITranslationProvider>)((IServiceProvider sp) => sp.GetRequiredService<TranslationProviderManager>()));
		services.AddSingleton<IPageClassifier, PageClassifier>();
		services.AddSingleton<ICompatibilityManager, CompatibilityManager>();
		services.AddSingleton<HotkeyManager>();
		services.AddSingleton<TranslationScheduler>();
		services.AddSingleton<TranslationOrchestrator>();
		return services.BuildServiceProvider();
	}
}
