// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Platform.Abstractions.Volumes;
using Files.Platform.Linux.Volumes;
using Files.Platform.Tests.SystemIntegration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Files.Platform.Tests.Volumes
{
	/// <summary>
	/// Unlocking encrypted volumes against <see cref="FakeUDisks2"/> on a private bus; no real device or prompt is involved.
	/// </summary>
	[TestClass]
	public sealed class UDisks2LuksTests
	{
		private const string Locked = "/org/freedesktop/UDisks2/block_devices/sde1";
		private const string Cleartext = "/org/freedesktop/UDisks2/block_devices/dm_sde1";

		private sealed class ScriptedPrompt(params string?[] answers) : IVolumePassphrasePrompt
		{
			private int next;

			public List<bool> Retries { get; } = new();

			public List<byte[]> Handed { get; } = new();

			public List<VolumeInfo> Volumes { get; } = new();

			public Task<byte[]?> RequestPassphraseAsync(VolumeInfo volume, bool retry, CancellationToken cancellationToken = default)
			{
				Retries.Add(retry);
				Volumes.Add(volume);
				var answer = answers[Math.Min(next++, answers.Length - 1)];
				if (answer is null)
					return Task.FromResult<byte[]?>(null);

				var bytes = Encoding.UTF8.GetBytes(answer);
				Handed.Add(bytes);
				return Task.FromResult<byte[]?>(bytes);
			}
		}

		private static async Task<(PrivateBus Bus, FakeUDisks2 Fake)> StartAsync()
		{
			var bus = PrivateBus.Start();
			var fake = await FakeUDisks2.StartAsync(bus.Address);
			fake.AddDrive("Generic_Flash", removable: true);
			fake.AddEncrypted("sde1", "Generic_Flash", "correct horse");
			return (bus, fake);
		}

		[TestMethod]
		public async Task LockedContainerIsListedAsLockedAndReplacedByItsCleartextOnceUnlocked()
		{
			var (bus, fake) = await StartAsync();
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address))
			{
				var locked = (await service.GetVolumesAsync()).Single();
				Assert.AreEqual(Locked, locked.Id);
				Assert.IsTrue(locked.IsLocked);
				Assert.IsFalse(locked.IsMounted);
				Assert.AreEqual("crypto_LUKS", locked.FileSystem);

				fake.AddCleartext("sde1");
				var after = (await service.GetVolumesAsync()).Single();
				Assert.AreEqual(Cleartext, after.Id);
				Assert.IsFalse(after.IsLocked);
				Assert.AreEqual("SECRET", after.Label);
			}
		}

		[TestMethod]
		public async Task MountUnlocksWithThePassphraseThenMountsTheCleartextDevice()
		{
			var (bus, fake) = await StartAsync();
			var prompt = new ScriptedPrompt("correct horse");
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, prompt))
			{
				var mountPoint = await service.MountAsync(Locked);

				Assert.AreEqual("/run/media/test/dm_sde1", mountPoint);
				var calls = fake.CallsSnapshot();
				Assert.AreEqual(2, calls.Count);
				Assert.AreEqual((Locked, "org.freedesktop.UDisks2.Encrypted", "Unlock"), (calls[0].Path, calls[0].Interface, calls[0].Member));
				Assert.AreEqual(false, calls[0].Options["auth.no_user_interaction"]);
				Assert.AreEqual((Cleartext, "org.freedesktop.UDisks2.Filesystem", "Mount"), (calls[1].Path, calls[1].Interface, calls[1].Member));
				CollectionAssert.AreEqual(new[] { "correct horse" }, fake.ReceivedPassphrases);
				Assert.IsFalse(prompt.Retries.Single());
				Assert.AreEqual(Locked, prompt.Volumes.Single().Id);
			}
		}

		[TestMethod]
		public async Task PassphraseBufferIsClearedAfterUse()
		{
			var (bus, fake) = await StartAsync();
			var prompt = new ScriptedPrompt("correct horse");
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, prompt))
			{
				await service.MountAsync(Locked);
				Assert.IsTrue(prompt.Handed.Single().All(b => b == 0));
			}
		}

		[TestMethod]
		public async Task WrongPassphrasePromptsAgainWithRetryAndThenSucceeds()
		{
			var (bus, fake) = await StartAsync();
			var prompt = new ScriptedPrompt("wrong", "correct horse");
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, prompt))
			{
				Assert.AreEqual("/run/media/test/dm_sde1", await service.MountAsync(Locked));

				CollectionAssert.AreEqual(new[] { false, true }, prompt.Retries);
				CollectionAssert.AreEqual(new[] { "wrong", "correct horse" }, fake.ReceivedPassphrases);
				Assert.IsTrue(prompt.Handed.All(h => h.All(b => b == 0)));
			}
		}

		[TestMethod]
		public async Task CancellingThePromptThrowsCancellationWithoutTouchingTheDevice()
		{
			var (bus, fake) = await StartAsync();
			var prompt = new ScriptedPrompt((string?)null);
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, prompt))
			{
				await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.MountAsync(Locked));
				Assert.AreEqual(0, fake.CallsSnapshot().Count);
			}
		}

		[TestMethod]
		public async Task CancellingAfterAWrongPassphraseStopsWithoutError()
		{
			var (bus, fake) = await StartAsync();
			var prompt = new ScriptedPrompt("wrong", null);
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, prompt))
			{
				await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.MountAsync(Locked));
				Assert.AreEqual(1, fake.CallsSnapshot().Count);
			}
		}

		[TestMethod]
		public async Task AlreadyUnlockedContainerMountsItsExistingCleartextWithoutPrompting()
		{
			var (bus, fake) = await StartAsync();
			fake.AddCleartext("sde1");
			var prompt = new ScriptedPrompt("never used");
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, prompt))
			{
				Assert.AreEqual("/run/media/test/dm_sde1", await service.MountAsync(Locked));

				Assert.AreEqual(0, prompt.Retries.Count);
				var call = fake.CallsSnapshot().Single();
				Assert.AreEqual((Cleartext, "Mount"), (call.Path, call.Member));
			}
		}

		[TestMethod]
		public async Task LockedContainerCannotBeMountedWithoutAPrompt()
		{
			var (bus, fake) = await StartAsync();
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address))
			{
				var ex = await Assert.ThrowsExactlyAsync<VolumeOperationException>(() => service.MountAsync(Locked));
				Assert.AreEqual(VolumeError.NotSupported, ex.Error);
				Assert.AreEqual(0, fake.CallsSnapshot().Count);
			}
		}

		[TestMethod]
		public async Task OtherUnlockFailuresAreReportedNotRetried()
		{
			var (bus, fake) = await StartAsync();
			fake.Errors["org.freedesktop.UDisks2.Encrypted.Unlock"] = "org.freedesktop.UDisks2.Error.NotAuthorizedCanObtain";
			var prompt = new ScriptedPrompt("correct horse");
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, prompt))
			{
				var ex = await Assert.ThrowsExactlyAsync<VolumeOperationException>(() => service.MountAsync(Locked));
				Assert.AreEqual(VolumeError.NotAuthorized, ex.Error);
				Assert.AreEqual(1, prompt.Retries.Count);
				Assert.IsFalse(prompt.Retries[0]);
				Assert.IsTrue(prompt.Handed.Single().All(b => b == 0));
			}
		}

		[TestMethod]
		public async Task PermissionErrorWithoutActivationWordingIsNotRetried()
		{
			var (bus, fake) = await StartAsync();
			fake.Errors["org.freedesktop.UDisks2.Encrypted.Unlock"] = "org.freedesktop.UDisks2.Error.Failed";
			fake.ErrorMessages["org.freedesktop.UDisks2.Encrypted.Unlock"] = "Error unlocking /dev/sde1: Failed to load dm-crypt: Operation not permitted";
			var prompt = new ScriptedPrompt("correct horse");
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, prompt))
			{
				var ex = await Assert.ThrowsExactlyAsync<VolumeOperationException>(() => service.MountAsync(Locked));
				Assert.AreEqual(VolumeError.Failed, ex.Error);
				Assert.AreEqual(1, prompt.Retries.Count);
			}
		}

		[TestMethod]
		public async Task WrongPassphraseStopsPromptingAfterTheRetryLimit()
		{
			var (bus, fake) = await StartAsync();
			var prompt = new ScriptedPrompt("wrong");
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, prompt))
			{
				var ex = await Assert.ThrowsExactlyAsync<VolumeOperationException>(() => service.MountAsync(Locked));
				Assert.AreEqual(VolumeError.IncorrectPassphrase, ex.Error);
				CollectionAssert.AreEqual(new[] { false, true, true, true, true }, prompt.Retries);
				Assert.AreEqual(5, fake.ReceivedPassphrases.Count);
			}
		}

		private const string LockMember = "org.freedesktop.UDisks2.Encrypted.Lock";

		private static List<string> Members(FakeUDisks2 fake) => fake.CallsSnapshot().Select(c => c.Interface.Split('.').Last() + "." + c.Member).ToList();

		[TestMethod]
		public async Task UnmountLocksTheContainerTheAppUnlocked()
		{
			var (bus, fake) = await StartAsync();
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, new ScriptedPrompt("correct horse")))
			{
				await service.MountAsync(Locked);
				await service.UnmountAsync(Cleartext);

				CollectionAssert.AreEqual(new[] { "Encrypted.Unlock", "Filesystem.Mount", "Filesystem.Unmount", "Encrypted.Lock" }, Members(fake).ToArray());
				Assert.AreEqual(Locked, fake.CallsSnapshot()[3].Path);
				Assert.IsTrue((await service.GetVolumesAsync()).Single().IsLocked);

				// The entry is forgotten: after an external unlock, unmounting does not lock again
				fake.AddCleartext("sde1");
				await service.UnmountAsync(Cleartext);
				Assert.AreEqual(1, Members(fake).Count(m => m == "Encrypted.Lock"));
			}
		}

		[TestMethod]
		public async Task EjectLocksTheContainerTheAppUnlocked()
		{
			var (bus, fake) = await StartAsync();
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, new ScriptedPrompt("correct horse")))
			{
				await service.MountAsync(Locked);
				await service.EjectAsync(Cleartext);

				var members = Members(fake);
				Assert.IsTrue(members.IndexOf("Filesystem.Unmount") < members.IndexOf("Encrypted.Lock"));
				Assert.AreEqual(Locked, fake.CallsSnapshot().Single(c => c.Member == "Lock").Path);

				// The drive is ejected after the container is locked, though the cleartext device has no drive of its own
				Assert.IsTrue(members.IndexOf("Encrypted.Lock") < members.IndexOf("Drive.Eject"));
			}
		}

		[TestMethod]
		public async Task EjectLocksAnUnmountedContainerTheAppUnlockedBeforeEjecting()
		{
			var (bus, fake) = await StartAsync();
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, new ScriptedPrompt("correct horse")))
			{
				await service.MountAsync(Locked);
				fake.SetMounts("dm_sde1");
				await service.EjectAsync(Cleartext);

				var members = Members(fake);
				Assert.IsFalse(members.Contains("Filesystem.Unmount"));
				Assert.IsTrue(members.IndexOf("Encrypted.Lock") < members.IndexOf("Drive.Eject"));
			}
		}

		[TestMethod]
		public async Task UnmountOfAnAlreadyUnmountedVolumeStillLocksWhatTheAppUnlocked()
		{
			var (bus, fake) = await StartAsync();
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, new ScriptedPrompt("correct horse")))
			{
				await service.MountAsync(Locked);
				fake.Errors["org.freedesktop.UDisks2.Filesystem.Unmount"] = "org.freedesktop.UDisks2.Error.NotMounted";
				await service.UnmountAsync(Cleartext);

				Assert.AreEqual(1, Members(fake).Count(m => m == "Encrypted.Lock"));
			}
		}

		[TestMethod]
		public async Task CleartextDeviceUsesTheDriveOfItsContainer()
		{
			var (bus, fake) = await StartAsync();
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address))
			{
				fake.AddCleartext("sde1");
				var volume = (await service.GetVolumesAsync()).Single();

				Assert.AreEqual("/org/freedesktop/UDisks2/drives/Generic_Flash", volume.DriveId);
				Assert.IsTrue(volume.CanEject);
			}
		}

		[TestMethod]
		public void UnlockTrackerOnlyForgetsTheContainerItTracked()
		{
			var tracker = new UDisks2UnlockTracker();
			Assert.IsNull(tracker.TryForget(Cleartext));

			tracker.Track(Cleartext, Locked);
			Assert.AreEqual(Locked, tracker.TryForget(Cleartext));
			Assert.IsNull(tracker.TryForget(Cleartext));
		}

		[TestMethod]
		public async Task ReusedCleartextNameForAnotherContainerIsNotLocked()
		{
			var (bus, fake) = await StartAsync();
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, new ScriptedPrompt("correct horse")))
			{
				await service.MountAsync(Locked);
				fake.AddEncrypted("sdf1", "Generic_Flash", "other");
				fake.SetMounts("dm_sde1");
				fake.Objects["/org/freedesktop/UDisks2/block_devices/dm_sde1"][UDisks2Parser.BlockInterface]["CryptoBackingDevice"] = new ObjPath("/org/freedesktop/UDisks2/block_devices/sdf1");
				await service.UnmountAsync(Cleartext);

				Assert.IsFalse(Members(fake).Contains("Encrypted.Lock"));
			}
		}

		[TestMethod]
		public async Task UnmountDoesNotLockAContainerThatWasAlreadyUnlocked()
		{
			var (bus, fake) = await StartAsync();
			fake.AddCleartext("sde1");
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, new ScriptedPrompt("correct horse")))
			{
				await service.MountAsync(Locked);
				await service.UnmountAsync(Cleartext);

				CollectionAssert.AreEqual(new[] { "Filesystem.Mount", "Filesystem.Unmount" }, Members(fake).ToArray());
			}
		}

		[TestMethod]
		public async Task LockFailureDoesNotFailTheUnmount()
		{
			var (bus, fake) = await StartAsync();
			fake.Errors[LockMember] = "org.freedesktop.UDisks2.Error.DeviceBusy";
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, new ScriptedPrompt("correct horse")))
			{
				await service.MountAsync(Locked);
				await service.UnmountAsync(Cleartext);

				Assert.AreEqual(1, Members(fake).Count(m => m == "Encrypted.Lock"));
				Assert.IsFalse((await service.GetVolumesAsync()).Single().IsMounted);
			}
		}

		[TestMethod]
		public async Task FailedUnmountDoesNotLock()
		{
			var (bus, fake) = await StartAsync();
			fake.Errors["org.freedesktop.UDisks2.Filesystem.Unmount"] = "org.freedesktop.UDisks2.Error.DeviceBusy";
			using (bus) using (fake) using (var service = new UDisks2VolumeService(bus.Address, new ScriptedPrompt("correct horse")))
			{
				await service.MountAsync(Locked);
				var ex = await Assert.ThrowsExactlyAsync<VolumeOperationException>(() => service.UnmountAsync(Cleartext));

				Assert.AreEqual(VolumeError.Busy, ex.Error);
				Assert.IsFalse(Members(fake).Contains("Encrypted.Lock"));
			}
		}

		[TestMethod]
		public void TranslateMapsOnlyPassphraseFailuresToIncorrectPassphrase()
		{
			static VolumeError Map(string name, string message) => UDisks2VolumeService.Translate(new DBusErrorReplyException(name, message)).Error;

			Assert.AreEqual(VolumeError.IncorrectPassphrase, Map("org.freedesktop.UDisks2.Error.Failed", "Error unlocking /dev/sde1: Failed to activate device: Operation not permitted"));
			Assert.AreEqual(VolumeError.IncorrectPassphrase, Map("org.freedesktop.UDisks2.Error.Failed", "Invalid passphrase"));
			Assert.AreEqual(VolumeError.Failed, Map("org.freedesktop.UDisks2.Error.Failed", "Failed to load dm-crypt: Operation not permitted"));
			Assert.AreEqual(VolumeError.Failed, Map("org.freedesktop.UDisks2.Error.Failed", "No space left on device"));
			Assert.AreEqual(VolumeError.NotAuthorized, Map("org.freedesktop.UDisks2.Error.NotAuthorizedCanObtain", "Not authorized"));
		}

		[TestMethod]
		public void FindCleartextFallsBackToTheBackingDeviceLinkWhenTheContainerPropertyIsStale()
		{
			var objects = new UDisks2Objects
			{
				[Locked] = new() { [UDisks2Parser.BlockInterface] = new(), [UDisks2Parser.EncryptedInterface] = new() { ["CleartextDevice"] = "/" } },
				[Cleartext] = new() { [UDisks2Parser.BlockInterface] = new() { ["CryptoBackingDevice"] = Locked } },
			};

			Assert.AreEqual(Cleartext, UDisks2Parser.FindCleartext(objects, Locked));
			objects.Remove(Cleartext);
			Assert.IsNull(UDisks2Parser.FindCleartext(objects, Locked));
		}
	}
}
