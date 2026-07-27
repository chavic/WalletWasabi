using System.IO;
using System.Threading.Tasks;
using Payjoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Payjoin;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WebClients.PayJoin;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

public class Bip77PayjoinTests
{
	/// <summary>
	/// Kill-and-resume over the SQLite event log: a sender session created through
	/// <see cref="WalletWasabi.Payjoin.SenderSessionPersister"/> must replay back to the same
	/// typestate from a fresh store handle (fresh process, same DB file), fallback tx intact.
	/// </summary>
	[Fact]
	public async Task SenderSession_PersistAndReplay_ResumesState()
	{
		string workDir = await Common.GetEmptyWorkDirAsync();
		string dbPath = Path.Combine(workDir, "sessions.sqlite");

		using var pjUri = PayjoinFfiTestHelpers.CreatePjUri();
		string endpoint = pjUri.PjEndpoint();

		// The dedup key parser must understand the fragment payjoin-ffi actually emits.
		Assert.True(Bip77UriParams.TryGetReceiverKey(endpoint, out var receiverKey));

		long sessionId;
		using (var store = PayjoinSenderSessionStore.FromFile(dbPath))
		{
			sessionId = store.CreateSession(endpoint, receiverKey, walletName: "test-wallet").Id;
			using var senderBuilder = new SenderBuilder(PayjoinFfiTestHelpers.OriginalPsbt, pjUri);
			using var initialTransition = senderBuilder.BuildRecommended(1000);
			using var withReplyKey = initialTransition.Save(new SenderSessionPersister(store, sessionId));
			Assert.NotNull(withReplyKey);
		}

		// "App restart": fresh connection over the same file.
		using (var store = PayjoinSenderSessionStore.FromFile(dbPath))
		{
			Assert.Equal(sessionId, Assert.Single(store.GetOpenSessions()).Id);

			using var replay = PayjoinMethods.ReplaySenderEventLog(new SenderSessionPersister(store, sessionId));
			using var state = replay.State();
			Assert.IsType<SendSession.WithReplyKey>(state);
			using var history = replay.SessionHistory();
			Assert.NotEmpty(history.FallbackTx());
		}
	}

	/// <summary>
	/// Downgrade reasons must read as plain language. ffi error objects are pointer-backed
	/// and cannot be fabricated from C#, so the mappable ones are produced through the ffi
	/// itself; the well-known BIP 78 receiver error codes
	/// (unavailable/not-enough-money/version-unsupported/original-psbt-rejected) only occur
	/// on a live response and are exercised by the payjoin-cli integration harness.
	/// </summary>
	[Fact]
	public void PayjoinErrors_MapToUserFriendlyStrings()
	{
		// A BIP 21 without pj → real PjNotSupported from the ffi.
		var pjNotSupported = Assert.ThrowsAny<Exception>(() =>
		{
			using var ffiUri = global::Payjoin.Uri.Parse("bitcoin:2MuyMrZHkbHbfjudmKUy45dU4P17pjG2szK");
			using var pjUri = ffiUri.CheckPjSupported();
		});
		Assert.Equal(
			"The payjoin link could not be understood, so the payment was sent as a normal transaction.",
			Bip77PayjoinClient.FriendlyFfiMessage(pjNotSupported));

		// Anything unexpected reads generically, never as a raw ffi message.
		Assert.Equal(
			"Payjoin failed, so the payment was sent as a normal transaction.",
			Bip77PayjoinClient.FriendlyFfiMessage(new InvalidOperationException("rust panic goo")));

		// PayjoinException messages pass through ToUserFriendlyString verbatim — they are
		// authored user-facing (dedup, relay exhaustion, poll window).
		var duplicate = new PayjoinDuplicateSessionException(
			new PayjoinSenderSessionRecord(1, "https://payjo.in/x#RK1A", "RK1A", "w", null, DateTimeOffset.UtcNow, IsCompleted: false));
		Assert.Equal("A payjoin to this link is already in progress.", duplicate.ToUserFriendlyString());

		var completedDuplicate = new PayjoinDuplicateSessionException(
			new PayjoinSenderSessionRecord(1, "https://payjo.in/x#RK1A", "RK1A", "w", null, DateTimeOffset.UtcNow, IsCompleted: true));
		Assert.Contains("already completed", completedDuplicate.ToUserFriendlyString());
	}
}
