using System.Linq;
using Payjoin;

namespace WalletWasabi.Payjoin;

/// <summary>
/// Adapts one store session's append-only event log to payjoin-ffi's
/// <see cref="JsonSenderSessionPersister"/> callback contract. The ffi calls
/// <see cref="Save"/> as part of every state transition (persist-before-act) and
/// <see cref="Close"/> when the session reaches a terminal state.
/// The ffi closes a session as soon as a proposal arrives or the fallback is handed over,
/// before anything is broadcast. The store row therefore stays open until the send flow
/// or the sweeper has broadcast, so a crash in between is still recovered.
/// </summary>
internal class SenderSessionPersister : JsonSenderSessionPersister
{
	private readonly PayjoinSenderSessionStore _store;
	private readonly long _sessionId;

	public SenderSessionPersister(PayjoinSenderSessionStore store, long sessionId)
	{
		_store = store;
		_sessionId = sessionId;
	}

	public void Save(string @event) => _store.AppendEvent(_sessionId, @event);

	public string[] Load() => _store.LoadEvents(_sessionId).ToArray();

	public void Close()
	{
		// The terminal event is already in the log; completion is the caller's, after broadcast.
	}
}
