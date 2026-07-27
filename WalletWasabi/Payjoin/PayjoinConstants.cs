using System.Collections.Generic;
using WalletWasabi.WebClients.Wasabi;

namespace WalletWasabi.Payjoin;

/// <summary>BIP 77 payjoin defaults. Kept in one file: the relay set is a trust/privacy
/// statement, so the whole decision stays reviewable as a single hunk.</summary>
public static class PayjoinConstants
{
	/// <summary>
	/// OHTTP relays used to reach the payjoin directory (payjo.in-ecosystem set, matching
	/// what Bull Bitcoin Mobile and Cake Wallet ship). Order is randomized per session;
	/// a relay that fails transport is quarantined for the session and the next is tried.
	/// </summary>
	public static readonly IReadOnlyList<string> DefaultOhttpRelays =
	[
		"https://pj.bobspacebkk.com",
		"https://ohttp.achow101.com",
		"https://ohttp.cakewallet.com",
	];

	/// <summary>How long the send confirm flow polls for a proposal before degrading to a
	/// plain send.</summary>
	public static readonly TimeSpan DefaultPollWindow = TimeSpan.FromSeconds(60);

	/// <summary>
	/// HTTP handler settings for OHTTP relay requests: send each request exactly once. A
	/// retry would resend identical ciphertext and let the relay link the attempts; the
	/// sender instead moves on to the next relay with a fresh encapsulation.
	/// </summary>
	public static readonly HttpClientHandlerConfiguration OhttpHttpClientConfiguration = new()
	{
		MaxAttempts = 1,
		TimeBeforeRetryingAfterNetworkError = TimeSpan.Zero,
		TimeBeforeRetryingAfterServerError = TimeSpan.Zero,
		TimeBeforeRetryingAfterTooManyRequests = TimeSpan.Zero,
	};
}
