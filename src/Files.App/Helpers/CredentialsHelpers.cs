using Files.Platform.Abstractions.Secrets;
using Windows.Security.Credentials;

namespace Files.App.Helpers
{
	internal sealed class CredentialsHelpers
	{
		// Desktop heads: the platform secret store (Secret Service on Linux) through ISecretStore; PasswordVault does not exist there
		private static ISecretStore? PlatformStore => OperatingSystem.IsWindows() ? null : Ioc.Default.GetService<ISecretStore>();

		public static void SavePassword(string resourceName, string username, string password)
		{
			if (PlatformStore is { } store)
			{
				store.Save(resourceName, username, password);
				return;
			}

			var vault = new PasswordVault();
			var credential = new PasswordCredential(resourceName, username, password);

			vault.Add(credential);
		}

		// Remove saved credentials from the vault
		public static void DeleteSavedPassword(string resourceName, string username)
		{
			if (PlatformStore is { } store)
			{
				store.Delete(resourceName, username);
				return;
			}

			var vault = new PasswordVault();
			var credential = vault.Retrieve(resourceName, username);

			vault.Remove(credential);
		}

		public static string GetPassword(string resourceName, string username)
		{
			if (PlatformStore is { } store)
				return store.Get(resourceName, username) ?? string.Empty;

			try
			{
				var vault = new PasswordVault();
				var credential = vault.Retrieve(resourceName, username);

				credential.RetrievePassword();

				return credential.Password;
			}
			// Thrown if the resource does not exist
			catch (Exception)
			{
				return string.Empty;
			}
		}
	}
}
