using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SteamCN.App.Services;

public static class SecureStorage
{
	private static readonly string SecureFilePath = Path.Combine(ServiceConfigurator.DataDirectory, "secure.dat");

	private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("SteamCNTranslator_v1.0");

	public static bool HasApiKey
	{
		get
		{
			if (!File.Exists(SecureFilePath))
			{
				return File.Exists(SecureFilePath + ".plain");
			}
			return true;
		}
	}

	public static void SaveApiKey(string apiKey)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SecureFilePath));
			byte[] bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), AdditionalEntropy, DataProtectionScope.CurrentUser);
			File.WriteAllBytes(SecureFilePath, bytes);
		}
		catch (Exception)
		{
			File.WriteAllText(SecureFilePath + ".plain", apiKey);
		}
	}

	public static string? LoadApiKey()
	{
		try
		{
			if (File.Exists(SecureFilePath))
			{
				byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(SecureFilePath), AdditionalEntropy, DataProtectionScope.CurrentUser);
				return Encoding.UTF8.GetString(bytes);
			}
			if (File.Exists(SecureFilePath + ".plain"))
			{
				return File.ReadAllText(SecureFilePath + ".plain");
			}
		}
		catch (CryptographicException)
		{
			try
			{
				File.Delete(SecureFilePath);
			}
			catch
			{
			}
		}
		catch
		{
		}
		return null;
	}

	public static void DeleteApiKey()
	{
		try
		{
			if (File.Exists(SecureFilePath))
			{
				File.Delete(SecureFilePath);
			}
			if (File.Exists(SecureFilePath + ".plain"))
			{
				File.Delete(SecureFilePath + ".plain");
			}
		}
		catch
		{
		}
	}
}
