using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Markup;
using System.Windows.Media;

namespace SteamCN.App;

internal static class AppUi
{
	internal static T Load<T>(string view) where T : class
	{
		using Stream stream = typeof(AppUi).Assembly.GetManifestResourceStream($"SteamCN.App.Ui.{view}.xaml")
			?? throw new InvalidOperationException($"界面资源缺失：{view}");
		return (T)XamlReader.Load(stream);
	}

	internal static T Require<T>(FrameworkElement view, string name) where T : FrameworkElement
	{
		var element = view.FindName(name) as T ?? throw new InvalidOperationException($"界面控件缺失：{name}");
		AutomationProperties.SetAutomationId(element, name);
		return element;
	}

	internal static Brush Brush(string name) => (Brush)Application.Current.FindResource(name);
}
