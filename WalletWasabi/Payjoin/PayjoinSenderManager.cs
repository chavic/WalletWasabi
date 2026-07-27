using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Bases;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Logging;
using PjFfi = global::Payjoin;

namespace WalletWasabi.Payjoin;

/// <summary>
/// Background sweeper for BIP 77 sender sessions that were left open — the app died
/// mid-negotiation before the send flow could broadcast anything. Policy:
/// the user asked to pay, so complete the payment. A late proposal cannot be signed here
/// (no wallet password in the background), therefore every abandoned session resolves to
/// broadcasting its fallback (original) tx and closing, except when the payjoin tx itself
/// is already known to the wallet. A session whose payment already settled (fallback or
/// payjoin tx known, or the fallback's inputs spent) closes without a broadcast. Broadcast
/// failures leave the session open for the next tick; sessions older than
/// <see cref="GiveUpAfter"/> are closed loudly.
/// </summary>
public class PayjoinSenderManager : PeriodicRunner
{
	private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(30);

	private readonly Network _network;
	private readonly Func<SmartTransaction, Task> _broadcastAsync;
	private readonly Func<uint256, bool> _isTransactionKnown;
	private readonly Func<OutPoint, uint256?> _getSpenderTxId;
	private readonly Func<string, HttpClient> _createHttpClient;
	private int _recoveryBroadcastCount;

	/// <param name="getSpenderTxId">The txid that spent a wallet outpoint, or null if unspent or unknown.</param>
	/// <param name="createHttpClient">Named clients that never retry a request on their own.</param>
	public PayjoinSenderManager(
		PayjoinSenderSessionStore sessionStore,
		Network network,
		Func<SmartTransaction, Task> broadcastAsync,
		Func<uint256, bool> isTransactionKnown,
		Func<OutPoint, uint256?> getSpenderTxId,
		Func<string, HttpClient> createHttpClient)
		: base(SweepPeriod)
	{
		SessionStore = sessionStore;
		_network = network;
		_broadcastAsync = broadcastAsync;
		_isTransactionKnown = isTransactionKnown;
		_getSpenderTxId = getSpenderTxId;
		_createHttpClient = createHttpClient;
	}

	public PayjoinSenderSessionStore SessionStore { get; }

	/// <summary>
	/// HTTP client for OHTTP relay requests. A retry would resend identical ciphertext,
	/// which lets a relay link the attempts, so these clients send each request once.
	/// </summary>
	public HttpClient CreateHttpClient(string name) => _createHttpClient(name);

	protected override async Task ActionAsync(CancellationToken cancel) => await SweepAsync(cancel).ConfigureAwait(false);

	internal async Task SweepAsync(CancellationToken cancel)
	{
		foreach (var session in SessionStore.GetOpenSessions())
		{
			cancel.ThrowIfCancellationRequested();

			// Sessions currently driven by the send flow, or completed since the snapshot
			// above, are not abandoned.
			if (!SessionStore.TryClaimForRecovery(session.Id))
			{
				continue;
			}

			try
			{
				await ResolveAbandonedSessionAsync(session, cancel).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex) when (session.CreatedAt < DateTimeOffset.UtcNow - GiveUpAfter)
			{
				Logger.LogError($"Giving up on payjoin session {session.Id} after {GiveUpAfter.TotalDays} days: {ex.Message} The fallback tx was NOT confirmed broadcast; hex: {session.FallbackTxHex}");
				SessionStore.CompleteSession(session.Id);
			}
			catch (Exception ex)
			{
				// Typically the broadcaster being offline; the session stays open for the next tick.
				Logger.LogWarning($"Could not resolve abandoned payjoin session {session.Id} yet: {ex.Message}");
			}
			finally
			{
				SessionStore.UnmarkActive(session.Id);
			}
		}
	}

	private async Task ResolveAbandonedSessionAsync(PayjoinSenderSessionRecord session, CancellationToken cancel)
	{
		var persister = new SenderSessionPersister(SessionStore, session.Id);

		PjFfi.SenderReplayResult replay;
		try
		{
			replay = PjFfi.PayjoinMethods.ReplaySenderEventLog(persister);
		}
		catch (Exception ex)
		{
			// Expired or unreadable event log; fall back to the tx hex stored at creation.
			Logger.LogWarning($"Payjoin session {session.Id} could not be replayed ({ex.Message}); resolving from the stored fallback tx.");
			await BroadcastStoredFallbackAsync(session, payjoinTxId: null, cancel).ConfigureAwait(false);
			SessionStore.CompleteSession(session.Id);
			return;
		}

		using var replayDisposal = replay;
		using var state = replay.State();
		switch (state)
		{
			case PjFfi.SendSession.Closed closed when closed.Inner.IsSuccess():
			{
				string? proposalPsbt = closed.Inner.SuccessPsbtBase64();
				uint256? payjoinTxId = proposalPsbt is { } psbt ? PSBT.Parse(psbt, _network).GetGlobalTransaction().GetHash() : null;

				// A proposal arrived but the send flow died before broadcasting it. It cannot
				// be signed here, so unless the payjoin tx made it out, the fallback completes the payment.
				await BroadcastStoredFallbackAsync(session, payjoinTxId, cancel).ConfigureAwait(false);
				SessionStore.CompleteSession(session.Id);
				return;
			}

			case PjFfi.SendSession.Closed:
				// Cancelled and handed to the send flow, which died before broadcasting the fallback.
				await BroadcastStoredFallbackAsync(session, payjoinTxId: null, cancel).ConfigureAwait(false);
				SessionStore.CompleteSession(session.Id);
				return;

			case PjFfi.SendSession.SenderPendingFallback pendingFallback:
				await BroadcastAndCloseAsync(pendingFallback.Inner, persister, session, cancel).ConfigureAwait(false);
				return;

			case PjFfi.SendSession.PollingForProposal polling:
				await CancelBroadcastCloseAsync(polling.Inner.Cancel(), persister, session, cancel).ConfigureAwait(false);
				return;

			case PjFfi.SendSession.WithReplyKey withReplyKey:
				await CancelBroadcastCloseAsync(withReplyKey.Inner.Cancel(), persister, session, cancel).ConfigureAwait(false);
				return;

			default:
				Logger.LogWarning($"Payjoin session {session.Id} replayed to an unexpected state; leaving it open.");
				return;
		}
	}

	private async Task CancelBroadcastCloseAsync(PjFfi.SenderCancelTransition cancelTransition, SenderSessionPersister persister, PayjoinSenderSessionRecord session, CancellationToken cancel)
	{
		using var cancelDisposal = cancelTransition;
		using var pendingFallback = cancelTransition.Save(persister);
		await BroadcastAndCloseAsync(pendingFallback, persister, session, cancel).ConfigureAwait(false);
	}

	private async Task BroadcastAndCloseAsync(PjFfi.SenderPendingFallback pendingFallback, SenderSessionPersister persister, PayjoinSenderSessionRecord session, CancellationToken cancel)
	{
		// Broadcast before closing: if the broadcast fails, the persisted state stays
		// PendingFallback and the next tick retries.
		await BroadcastFallbackAsync(session, Transaction.Load(pendingFallback.FallbackTx(), _network), payjoinTxId: null, cancel).ConfigureAwait(false);
		using var broadcastedTransition = pendingFallback.Close();
		broadcastedTransition.Save(persister);
		SessionStore.CompleteSession(session.Id);
	}

	public override void Dispose()
	{
		SessionStore.Dispose();
		base.Dispose();
	}

	private async Task BroadcastStoredFallbackAsync(PayjoinSenderSessionRecord session, uint256? payjoinTxId, CancellationToken cancel)
	{
		if (session.FallbackTxHex is not { } fallbackTxHex)
		{
			Logger.LogWarning($"Payjoin session {session.Id} has no stored fallback tx; closing without broadcast.");
			return;
		}

		await BroadcastFallbackAsync(session, Transaction.Parse(fallbackTxHex, _network), payjoinTxId, cancel).ConfigureAwait(false);
	}

	private async Task BroadcastFallbackAsync(PayjoinSenderSessionRecord session, Transaction fallbackTx, uint256? payjoinTxId, CancellationToken cancel)
	{
		cancel.ThrowIfCancellationRequested();

		if (IsSettled(session, fallbackTx, payjoinTxId))
		{
			return;
		}

		// Recovery metric: frequent crash-recovery broadcasts are the signal for
		// prioritizing true pending-async sends.
		int count = Interlocked.Increment(ref _recoveryBroadcastCount);
		Logger.LogInfo($"Broadcasting payjoin fallback tx {fallbackTx.GetHash()} (crash-recovery broadcast #{count} this run).");

		await _broadcastAsync(new SmartTransaction(fallbackTx)).ConfigureAwait(false);
	}

	/// <summary>
	/// Whether the payment no longer depends on this session: the receiver (or an earlier
	/// run) already broadcast the fallback, the payjoin tx made it out, or the fallback's
	/// inputs were spent. Retrying such a broadcast can never succeed.
	/// </summary>
	private bool IsSettled(PayjoinSenderSessionRecord session, Transaction fallbackTx, uint256? payjoinTxId)
	{
		uint256 fallbackTxId = fallbackTx.GetHash();
		if (_isTransactionKnown(fallbackTxId) || (payjoinTxId is { } knownPayjoinTxId && _isTransactionKnown(knownPayjoinTxId)))
		{
			Logger.LogInfo($"Payjoin session {session.Id} already settled on-chain; closing without broadcast.");
			return true;
		}

		foreach (var input in fallbackTx.Inputs)
		{
			if (_getSpenderTxId(input.PrevOut) is not { } spenderTxId)
			{
				continue;
			}

			if (spenderTxId == fallbackTxId || spenderTxId == payjoinTxId)
			{
				Logger.LogInfo($"Payjoin session {session.Id} already settled by {spenderTxId}; closing without broadcast.");
			}
			else
			{
				Logger.LogError($"The inputs of payjoin session {session.Id} were spent by {spenderTxId}, which is neither its fallback nor its payjoin tx, so this session can no longer make the payment. Closing it; fallback tx hex: {fallbackTx.ToHex()}");
			}

			return true;
		}

		return false;
	}
}
