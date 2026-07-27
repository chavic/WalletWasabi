using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using NBitcoin;
using WalletWasabi.Tests.Helpers;
using System.Linq;
using WalletWasabi.Payjoin;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

public class PayjoinSenderSessionStoreTests
{
	[Fact]
	public void DeduplicatesOnEndpointAndReceiverKeyIncludingCompleted()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");

		var record = store.CreateSession("https://payjo.in/a#RK1AAA+OH1BBB", "RK1AAA", "wallet");

		// Same endpoint, open session.
		var exOpen = Assert.Throws<PayjoinDuplicateSessionException>(
			() => store.CreateSession("https://payjo.in/a#RK1AAA+OH1BBB", "RK1OTHER", "wallet"));
		Assert.False(exOpen.Existing.IsCompleted);

		// Same receiver key, different endpoint.
		Assert.Throws<PayjoinDuplicateSessionException>(
			() => store.CreateSession("https://other.example/b#RK1AAA+OH1BBB", "RK1AAA", "wallet"));

		// Dedup persists after completion (address/HPKE-key reuse prevention).
		store.CompleteSession(record.Id);
		var exCompleted = Assert.Throws<PayjoinDuplicateSessionException>(
			() => store.CreateSession("https://payjo.in/a#RK1AAA+OH1BBB", "RK1AAA", "wallet"));
		Assert.True(exCompleted.Existing.IsCompleted);

		// A genuinely new session is fine.
		store.CreateSession("https://payjo.in/c#RK1CCC+OH1BBB", "RK1CCC", "wallet");
	}

	[Fact]
	public void EventLogIsOrderedAndCompletionIsIdempotent()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");

		var first = store.CreateSession("https://payjo.in/a#RK1AAA", "RK1AAA", "wallet-1");
		var second = store.CreateSession("https://payjo.in/b#RK1BBB", "RK1BBB", "wallet-2");

		store.AppendEvent(first.Id, "e1");
		store.AppendEvent(second.Id, "other");
		store.AppendEvent(first.Id, "e2");
		store.AppendEvent(first.Id, "e3");

		Assert.Equal(new[] { "e1", "e2", "e3" }, store.LoadEvents(first.Id));

		Assert.Equal(2, store.GetOpenSessions().Count);
		store.CompleteSession(first.Id);
		store.CompleteSession(first.Id); // Idempotent.
		Assert.Equal(second.Id, Assert.Single(store.GetOpenSessions()).Id);

		Assert.True(store.TryFindSession(first.Endpoint, first.ReceiverKey, out var found));
		Assert.True(found.IsCompleted);

		store.SetFallbackTx(second.Id, "beef");
		Assert.Equal("beef", store.GetOpenSessions().Single().FallbackTxHex);
	}

	[Fact]
	public void ActiveSessionGuard()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");
		var record = store.CreateSession("https://payjo.in/a#RK1AAA", "RK1AAA", "wallet");

		Assert.False(store.IsActive(record.Id));
		store.MarkActive(record.Id);
		Assert.True(store.IsActive(record.Id));
		store.UnmarkActive(record.Id);
		Assert.False(store.IsActive(record.Id));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InitialEventFailure_RollsBackSessionAndCannotRecoverBroadcastAsync(bool failAfterFirstEvent)
	{
		string workDir = await Common.GetEmptyWorkDirAsync(callerMemberName: $"{nameof(InitialEventFailure_RollsBackSessionAndCannotRecoverBroadcastAsync)}_{failAfterFirstEvent}");
		string dbPath = Path.Combine(workDir, "sessions.sqlite");
		const string endpoint = "https://payjo.in/atomic#RK1ATOMIC";
		const string receiverKey = "RK1ATOMIC";
		string fallback = PSBT.Parse(PayjoinFfiTestHelpers.OriginalPsbt, Network.TestNet).ExtractTransaction().ToHex();

		using (var store = PayjoinSenderSessionStore.FromFile(dbPath))
		using (var faultConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
		{
			faultConnection.Open();
			using var command = faultConnection.CreateCommand();
			command.CommandText = """
				CREATE TRIGGER fail_initial_event BEFORE INSERT ON sender_session_event
				WHEN NEW.event_json = 'fail'
				BEGIN SELECT RAISE(ABORT, 'injected initial persistence failure'); END;
				""";
			command.ExecuteNonQuery();

			string[] events = failAfterFirstEvent ? ["first", "fail", "last"] : ["fail", "last"];
			Assert.Throws<SqliteException>(() => store.CreateActiveSession(endpoint, receiverKey, "wallet", fallback, events));
			Assert.False(store.TryFindSession(endpoint, receiverKey, out _));
			Assert.Empty(store.GetOpenSessions());
			Assert.False(store.IsActive(1));
			command.CommandText = "SELECT COUNT(*) FROM sender_session_event;";
			Assert.Equal(0L, command.ExecuteScalar());
		}

		// A fresh handle simulates restart; neither metadata nor a partially inserted
		// event log can survive the rollback and cause a fallback broadcast.
		using var reopened = PayjoinSenderSessionStore.FromFile(dbPath);
		Assert.False(reopened.TryFindSession(endpoint, receiverKey, out _));
		Assert.Empty(reopened.GetOpenSessions());
		int broadcasts = 0;
		using var manager = new PayjoinSenderManager(reopened, Network.TestNet, _ =>
		{
			broadcasts++;
			return Task.CompletedTask;
		}, _ => false);
		await manager.SweepAsync(CancellationToken.None);
		Assert.Equal(0, broadcasts);
	}

	[Fact]
	public void InitialSessionCommit_PersistsEventsAndActiveGuard()
	{
		using var store = PayjoinSenderSessionStore.FromFile(":memory:");
		string[] events = ["first", "second"];
		var session = store.CreateActiveSession("https://payjo.in/atomic", "RK1ATOMIC", "wallet", "beef", events);

		Assert.True(store.IsActive(session.Id));
		Assert.Equal(events, store.LoadEvents(session.Id));
		Assert.Equal("beef", Assert.Single(store.GetOpenSessions()).FallbackTxHex);
		Assert.Throws<PayjoinDuplicateSessionException>(() =>
			store.CreateActiveSession(session.Endpoint, session.ReceiverKey, "wallet", "other", ["replacement"]));
		Assert.Equal(events, store.LoadEvents(session.Id));
		Assert.True(store.IsActive(session.Id));
	}

}
