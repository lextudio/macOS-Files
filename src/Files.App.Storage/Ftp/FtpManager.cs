// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Secrets;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Files.App.Storage
{
	public static class FtpManager
	{
		/// <summary>
		/// Credentials by host. <see cref="FtpCredentialCache.SetAsync"/> also saves the password in the secret store, when one is configured.
		/// </summary>
		public static readonly FtpCredentialCache Credentials = new();

		public static readonly NetworkCredential Anonymous = new("anonymous", "anonymous");

		/// <summary>
		/// The store passwords are persisted in. Set once at startup; passwords are never written anywhere else.
		/// </summary>
		public static ISecretStore? SecretStore { get; set; }

		/// <summary>
		/// Keeps credentials embedded in <paramref name="url"/> for this session. Call before the user info is stripped from the id.
		/// </summary>
		public static void RememberUrlCredential(FtpUrl url)
		{
			if (url.GetCredential() is { } credential)
				Credentials.SetFromUrl(url.GetCredentialKey(), credential);
		}

		private static readonly HashSet<string> _cleartextApproved = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Records that the user explicitly accepted sending a password unencrypted to this scheme+host+port
		/// (<see cref="FtpUrl.GetCredentialKey"/>), for this session only.
		/// </summary>
		public static void ApproveCleartext(string credentialKey)
		{
			lock (_cleartextApproved)
				_cleartextApproved.Add(credentialKey);
		}

		/// <summary>
		/// Whether the connection must be encrypted without fallback: always for ftps:// and ftpes://, and for plain ftp://
		/// whenever a password would be sent, unless the user approved cleartext for this host and port.
		/// </summary>
		public static bool RequiresTls(FtpUrl url, bool anonymous)
			=> url.IsImplicitTls || url.IsExplicitTls || (!anonymous && !IsCleartextApproved(url.GetCredentialKey()));

		public static bool IsCleartextApproved(string credentialKey)
		{
			lock (_cleartextApproved)
				return _cleartextApproved.Contains(credentialKey);
		}
	}

	/// <summary>
	/// Session cache of FTP credentials backed by <see cref="FtpManager.SecretStore"/>.
	/// </summary>
	public sealed class FtpCredentialCache : IReadOnlyDictionary<string, NetworkCredential>
	{
		private const string LastUserAccount = "(last user)";

		private readonly Dictionary<string, NetworkCredential> _session = new(StringComparer.OrdinalIgnoreCase);
		// Credentials taken from URLs: kept per scope for the session, never persisted and never replacing a saved credential
		private readonly Dictionary<string, NetworkCredential> _fromUrl = new(StringComparer.OrdinalIgnoreCase);
		private readonly object _lock = new();

		public NetworkCredential this[string host]
			=> TryGetValue(host, out var credential) ? credential : throw new KeyNotFoundException();

		/// <summary>
		/// Keeps credentials taken from a URL for this session only. They are never persisted and never override a saved
		/// credential for the same scope.
		/// </summary>
		public void SetFromUrl(string credentialKey, NetworkCredential credential)
		{
			lock (_lock)
				_fromUrl[credentialKey] = credential;
		}

		public NetworkCredential Get(string host, NetworkCredential defaultValue)
			=> TryGetValue(host, out var credential) ? credential : defaultValue;

		/// <summary>
		/// Removes the credential for <paramref name="host"/> from the session and the secret store.
		/// </summary>
		public async Task RemoveAsync(string host)
		{
			lock (_lock)
			{
				_session.Remove(host);
				_fromUrl.Remove(host);
			}

			if (FtpManager.SecretStore is { } store)
			{
				try
				{
					var resource = GetResource(host);
					if (await store.GetAsync(resource, LastUserAccount).ConfigureAwait(false) is { } user)
						await store.DeleteAsync(resource, user).ConfigureAwait(false);
					await store.DeleteAsync(resource, LastUserAccount).ConfigureAwait(false);
				}
				catch (Exception)
				{
					// Secret store unavailable; nothing persisted to remove
				}
			}
		}

		/// <summary>
		/// Looks up the credential held in memory for this session; the secret store is not consulted, see <see cref="TryGetSavedAsync"/>.
		/// </summary>
		public bool TryGetValue(string host, out NetworkCredential value)
		{
			lock (_lock)
			{
				if (_session.TryGetValue(host, out value!) || _fromUrl.TryGetValue(host, out value!))
					return true;
			}

			value = null!;
			return false;
		}

		/// <summary>
		/// Finds the credential for <paramref name="host"/>: this session's, then the saved one, then one taken from a URL.
		/// Reading the secret store may wait for the user to unlock the keyring, so this never blocks.
		/// </summary>
		public async Task<NetworkCredential?> TryGetSavedAsync(string host)
		{
			lock (_lock)
			{
				if (_session.TryGetValue(host, out var session))
					return session;
			}

			// Legacy bare-host keys have no scheme/port scope and are never persisted or read from the store
			var scoped = host.Contains("://", StringComparison.Ordinal);

			if (scoped && FtpManager.SecretStore is { } store)
			{
				try
				{
					var resource = GetResource(host);
					if (await store.GetAsync(resource, LastUserAccount).ConfigureAwait(false) is { Length: > 0 } user
						&& await store.GetAsync(resource, user).ConfigureAwait(false) is { } password)
					{
						// Not cached: the password stays in memory only as long as the caller needs it
						return new NetworkCredential(user, password);
					}
				}
				catch (Exception)
				{
					// Secret store unavailable; behave as if nothing was saved
				}
			}

			lock (_lock)
			{
				return _fromUrl.TryGetValue(host, out var fromUrl) ? fromUrl : null;
			}
		}

		/// <summary>
		/// Keeps <paramref name="credential"/> for this session and saves it in the secret store when one is configured.
		/// </summary>
		public async Task SetAsync(string host, NetworkCredential credential)
		{
			lock (_lock)
			{
				_session[host] = credential;
			}

			if (!host.Contains("://", StringComparison.Ordinal) || FtpManager.SecretStore is not { } store || string.IsNullOrEmpty(credential.UserName))
				return;

			try
			{
				var resource = GetResource(host);
				await store.SaveAsync(resource, credential.UserName, credential.Password).ConfigureAwait(false);
				await store.SaveAsync(resource, LastUserAccount, credential.UserName).ConfigureAwait(false);
			}
			catch (Exception)
			{
				// Keep the credential for this session when the store can't persist it
			}
		}

		private static string GetResource(string key) => "Files FTP " + key;

		public bool ContainsKey(string host) => TryGetValue(host, out _);

		public IEnumerable<string> Keys
		{
			get { lock (_lock) { return _session.Keys.ToArray(); } }
		}

		public IEnumerable<NetworkCredential> Values
		{
			get { lock (_lock) { return _session.Values.ToArray(); } }
		}

		public int Count
		{
			get { lock (_lock) { return _session.Count; } }
		}

		public IEnumerator<KeyValuePair<string, NetworkCredential>> GetEnumerator()
		{
			KeyValuePair<string, NetworkCredential>[] snapshot;
			lock (_lock)
			{
				snapshot = _session.ToArray();
			}

			return ((IEnumerable<KeyValuePair<string, NetworkCredential>>)snapshot).GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
