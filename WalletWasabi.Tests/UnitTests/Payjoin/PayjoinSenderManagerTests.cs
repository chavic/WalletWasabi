using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Payjoin;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Payjoin;
using Xunit;
using OutPoint = NBitcoin.OutPoint;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

/// <summary>
/// The background sweeper's crash-recovery contract: any sender session left open by a
/// dead process resolves to broadcasting its fallback (original) tx and closing,
/// while in-flight sessions and broadcast outages are left alone.
/// </summary>
public class PayjoinSenderManagerTests
{
	private static readonly Transaction FallbackTx = PSBT.Parse(PayjoinFfiTestHelpers.OriginalPsbt, Network.TestNet).ExtractTransaction();

	private static PayjoinSenderManager CreateManager(
		PayjoinSenderSessionStore store,
		List<SmartTransaction> broadcasts,
		Func<uint256, bool>? isTransactionKnown = null,
		Func<OutPoint, uint256?>? getSpenderTxId = null) =>
		new(
			store,
			Network.TestNet,
			tx =>
			{
				broadcasts.Add(tx);
				return Task.CompletedTask;
			},
			isTransactionKnown ?? (_ => false),
			getSpenderTxId ?? (_ => null),
			_ => throw new NotSupportedException());

	private static long CreateAbandonedFfiSession(PayjoinSenderSessionStore store)
	{
		using var pjUri = PayjoinFfiTestHelpers.CreatePjUri();
		string endpoint = pjUri.PjEndpoint();
		Assert.True(Bip77UriParams.TryGetReceiverKey(endpoint, out var receiverKey));

		long sessionId = store.CreateSession(endpoint, receiverKey, "test-wallet", FallbackTx.ToHex()).Id;
		using var senderBuilder = new SenderBuilder(PayjoinFfiTestHelpers.OriginalPsbt, pjUri);
		using var initialTransition = senderBuilder.BuildRecommended(1000);
		using var withReplyKey = initialTransition.Save(new SenderSessionPersister(store, sessionId));
		return sessionId;
	}

	[Fact]
	public async Task AbandonedSession_BroadcastsFallbackAndClosesAsync()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");
		long sessionId = CreateAbandonedFfiSession(store);
		List<SmartTransaction> broadcasts = new();
		using var manager = CreateManager(store, broadcasts);

		await manager.SweepAsync(CancellationToken.None);

		Assert.Single(broadcasts);
		Assert.Empty(store.GetOpenSessions());

		// The ffi event log is terminal too: replay yields a closed session.
		using var replay = PayjoinMethods.ReplaySenderEventLog(new SenderSessionPersister(store, sessionId));
		using var state = replay.State();
		Assert.IsType<SendSession.Closed>(state);
	}

	[Fact]
	public async Task ActiveSession_IsLeftAloneAsync()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");
		long sessionId = CreateAbandonedFfiSession(store);
		store.MarkActive(sessionId);
		List<SmartTransaction> broadcasts = new();
		using var manager = CreateManager(store, broadcasts);

		await manager.SweepAsync(CancellationToken.None);

		Assert.Empty(broadcasts);
		Assert.Single(store.GetOpenSessions());
	}

	[Fact]
	public async Task UnreplayableSession_BroadcastsStoredFallbackHexAsync()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");

		var fallbackTx = Network.TestNet.CreateTransaction();
		fallbackTx.Inputs.Add(new OutPoint(uint256.One, 0));
		using Key key = new();
		fallbackTx.Outputs.Add(Money.Coins(0.1m), key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));

		// A session row with a fallback tx but an empty (unreplayable) event log.
		store.CreateSession("https://payjo.in/x#RK1XXX+OH1YYY", "RK1XXX", "test-wallet", fallbackTx.ToHex());

		List<SmartTransaction> broadcasts = new();
		using var manager = CreateManager(store, broadcasts);

		await manager.SweepAsync(CancellationToken.None);

		Assert.Equal(fallbackTx.GetHash(), Assert.Single(broadcasts).Transaction.GetHash());
		Assert.Empty(store.GetOpenSessions());
	}

	[Fact]
	public async Task BroadcastFailure_LeavesSessionOpenForRetryAsync()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");
		CreateAbandonedFfiSession(store);

		bool broadcasterOnline = false;
		List<SmartTransaction> broadcasts = new();
		using var manager = new PayjoinSenderManager(
			store,
			Network.TestNet,
			tx =>
			{
				if (!broadcasterOnline)
				{
					throw new InvalidOperationException("offline");
				}

				broadcasts.Add(tx);
				return Task.CompletedTask;
			},
			isTransactionKnown: _ => false,
			getSpenderTxId: _ => null,
			createHttpClient: _ => throw new NotSupportedException());

		await manager.SweepAsync(CancellationToken.None);
		Assert.Empty(broadcasts);
		Assert.Single(store.GetOpenSessions()); // Still open, will be retried.

		broadcasterOnline = true;
		await manager.SweepAsync(CancellationToken.None);
		Assert.Single(broadcasts);
		Assert.Empty(store.GetOpenSessions());
	}

	/// <summary>
	/// The send flow cancelled and closed the session to broadcast the original itself, then
	/// died before doing so. The closed session is still open in the store, so the sweeper
	/// completes the payment with the stored fallback.
	/// </summary>
	[Fact]
	public async Task ClosedButUnbroadcastSession_BroadcastsStoredFallbackAsync()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");
		long sessionId = CreateAbandonedFfiSession(store);
		var persister = new SenderSessionPersister(store, sessionId);
		using (var replay = PayjoinMethods.ReplaySenderEventLog(persister))
		using (var state = replay.State())
		{
			var withReplyKey = Assert.IsType<SendSession.WithReplyKey>(state);
			using var cancelTransition = withReplyKey.Inner.Cancel();
			using var pendingFallback = cancelTransition.Save(persister);
			using var closeTransition = pendingFallback.Close();
			closeTransition.Save(persister);
		}

		Assert.Single(store.GetOpenSessions()); // ffi Close does not complete the store row.
		List<SmartTransaction> broadcasts = new();
		using var manager = CreateManager(store, broadcasts);

		await manager.SweepAsync(CancellationToken.None);

		Assert.Equal(FallbackTx.GetHash(), Assert.Single(broadcasts).GetHash());
		Assert.Empty(store.GetOpenSessions());
	}

	[Fact]
	public async Task FallbackAlreadyKnown_ClosesWithoutBroadcastAsync()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");
		CreateAbandonedFfiSession(store);
		List<SmartTransaction> broadcasts = new();

		// E.g. the receiver broadcast the fallback, as BIP 77 allows.
		using var manager = CreateManager(store, broadcasts, isTransactionKnown: txid => txid == FallbackTx.GetHash());
		await manager.SweepAsync(CancellationToken.None);

		Assert.Empty(broadcasts);
		Assert.Empty(store.GetOpenSessions());
	}

	[Fact]
	public async Task FallbackInputsSpentElsewhere_ClosesWithoutBroadcastAsync()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");
		CreateAbandonedFfiSession(store);
		List<SmartTransaction> broadcasts = new();

		// The user spent the coins after the crash; the fallback can never confirm.
		OutPoint spentInput = FallbackTx.Inputs[0].PrevOut;
		using var manager = CreateManager(store, broadcasts, getSpenderTxId: outPoint => outPoint == spentInput ? uint256.One : null);
		await manager.SweepAsync(CancellationToken.None);

		Assert.Empty(broadcasts);
		Assert.Empty(store.GetOpenSessions());
	}
}
