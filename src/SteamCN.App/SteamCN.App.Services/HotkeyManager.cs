using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SteamCN.App.Services;

public class HotkeyManager : IDisposable
{
	private readonly Dictionary<int, Action> _hotkeys = new Dictionary<int, Action>();

	private readonly Dictionary<string, int> _hotkeyIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private HwndSource? _hwndSource;

	private bool _disposed;

	private int _nextId = 36864;

	private const uint MOD_ALT = 1u;

	private const uint MOD_CONTROL = 2u;

	private const uint MOD_SHIFT = 4u;

	private const uint MOD_WIN = 8u;

	public event Action<string>? HotkeyPressed;

	public void Initialize(nint windowHandle)
	{
		_hwndSource = HwndSource.FromHwnd(windowHandle);
		if (_hwndSource != null)
		{
			_hwndSource.AddHook(WndProc);
		}
	}

	public bool Register(string name, string keyCombo)
	{
		if (_hwndSource == null)
		{
			return false;
		}
		if (_hotkeyIds.ContainsKey(name))
		{
			Unregister(name);
		}
		var (fsModifiers, num) = ParseKeyCombo(keyCombo);
		if (num == 0)
		{
			return false;
		}
		int num2 = _nextId++;
		if (!RegisterHotKey(_hwndSource.Handle, num2, fsModifiers, num))
		{
			return false;
		}
		_hotkeys[num2] = () =>
		{
			HotkeyPressed?.Invoke(name);
		};
		_hotkeyIds[name] = num2;
		return true;
	}

	public void Unregister(string name)
	{
		if (_hotkeyIds.TryGetValue(name, out var value))
		{
			if (_hwndSource != null)
			{
				UnregisterHotKey(_hwndSource.Handle, value);
			}
			_hotkeys.Remove(value);
			_hotkeyIds.Remove(name);
		}
	}

	public void UnregisterAll()
	{
		if (_hwndSource != null)
		{
			foreach (int item in _hotkeys.Keys.ToList())
			{
				UnregisterHotKey(_hwndSource.Handle, item);
			}
		}
		_hotkeys.Clear();
		_hotkeyIds.Clear();
	}

	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg == 786)
		{
			int key = ((IntPtr)wParam).ToInt32();
			if (_hotkeys.TryGetValue(key, out Action value))
			{
				value();
				handled = true;
			}
		}
		return IntPtr.Zero;
	}

	internal static (uint Modifiers, uint Vk) ParseKeyCombo(string keyCombo)
	{
		if (string.IsNullOrWhiteSpace(keyCombo))
		{
			return (Modifiers: 0u, Vk: 0u);
		}
		uint num = 0u;
		uint num2 = 0u;
		string[] array = keyCombo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string text in array)
		{
			switch (text.ToUpperInvariant())
			{
			case "CTRL":
			case "CONTROL":
				num |= 2;
				break;
			case "ALT":
				num |= 1;
				break;
			case "SHIFT":
				num |= 4;
				break;
			case "WIN":
			case "WINDOWS":
				num |= 8;
				break;
			default:
				num2 = KeyToVk(text);
				break;
			}
		}
		if (num2 == 0)
		{
			return (Modifiers: 0u, Vk: 0u);
		}
		if (num == 0)
		{
			num = 1u;
		}
		return (Modifiers: num, Vk: num2);
	}

	private static uint KeyToVk(string key)
	{
		if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
		{
			return char.ToUpper(key[0]);
		}
		if (key.StartsWith('F') && key.Length <= 3)
		{
			string text = key;
			if (int.TryParse(text.Substring(1, text.Length - 1), out var result) && result >= 1 && result <= 24)
			{
				return (uint)(112 + result - 1);
			}
		}
		switch (key.ToUpperInvariant())
		{
		case "SPACE":
			return 32u;
		case "ENTER":
			return 13u;
		case "ESC":
		case "ESCAPE":
			return 27u;
		case "TAB":
			return 9u;
		case "HOME":
			return 36u;
		case "END":
			return 35u;
		case "PAGEUP":
		case "PGUP":
			return 33u;
		case "PGDN":
		case "PAGEDOWN":
			return 34u;
		case "INSERT":
			return 45u;
		case "DELETE":
			return 46u;
		default:
			return 0u;
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			UnregisterAll();
			_hwndSource?.RemoveHook(WndProc);
			GC.SuppressFinalize(this);
		}
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterHotKey(nint hWnd, int id);
}
