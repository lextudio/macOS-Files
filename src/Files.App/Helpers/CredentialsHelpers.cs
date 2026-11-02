using Files.Platform.Abstractions.Secrets;
using Windows.Security.Credentials;

namespace Files.App.Helpers
{
	internal sealed class CredentialsHelpers
	{
		// Linux: the Secret Service (libsecret backend) through ISecretStore; PasswordVault does not exist there
		private static ISecretStore? LinuxStore => OperatingSystem.IsLinux() ? Ioc.Default.GetService<ISecretStore>() : null;

		public static async Task SavePasswordAsync(string resourceName, string username, string password)
		{
			if (LinuxStore is { } store)
			{
				await store.SaveAsync(resourceName, username, password);
				return;
			}

			var vault = new PasswordVault();
			var credential = new PasswordCredential(resourceName, username, password);

			vault.Add(credential);
		}

		// Remove saved credentials from the vault
		public static async Task DeleteSavedPasswordAsync(string resourceName, string username)
		{
			if (LinuxStore is { } store)
			{
				await store.DeleteAsync(resourceName, username);
				return;
			}

			var vault = new PasswordVault();
			var credential = vault.Retrieve(resourceName, username);

			vault.Remove(credential);
		}

		public static async Task<string> GetPasswordAsync(string resourceName, string username)
		{
			if (LinuxStore is { } store)
				return await store.GetAsync(resourceName, username) ?? string.Empty;

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
