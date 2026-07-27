using System.Reflection;
using System.Runtime.CompilerServices;
using WalletWasabi.Fluent.Models;
using WalletWasabi.Fluent.Models.ClientConfig;
using WalletWasabi.Fluent.Models.FileSystem;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.SearchBar.Sources;
using WalletWasabi.Announcements;

namespace WalletWasabi.Tests.UnitTests.ViewModels.TestDoubles;

/// <summary>
/// Builds a real <see cref="UiContext"/> over a <see cref="FakeServices"/> for view-model
/// unit tests, mirroring the composition in <c>App.CreateUiContext</c>.
/// </summary>
public static class FakeUiContext
{
	public static UiContext Create(FakeServices services)
	{
		var amountProvider = new AmountProvider(services);
		var applicationSettings = CreateApplicationSettings(services);
		var torStatusChecker = new TorStatusCheckerModel(services);

		return new UiContext(
			services,
			new QrCodeGenerator(),
			new QrCodeReader(),
			new UiClipboard(),
			new WalletRepository(services, amountProvider),
			new CoinjoinModel(services),
			new HardwareWalletInterface(services),
			new FileSystemModel(),
			new ClientConfigModel(services),
			applicationSettings,
			new TransactionBroadcasterModel(services, applicationSettings.Network),
			amountProvider,
			new EditableSearchSource(),
			torStatusChecker,
			new HealthMonitor(services, torStatusChecker),
			new ReleaseHighlights());
	}

	private static ApplicationSettings CreateApplicationSettings(FakeServices services)
	{
		// The real constructor subscribes to startup-item changes immediately. These
		// payjoin tests only read Network and AutoPaste, so avoid OS/config side effects.
		var settings = (ApplicationSettings)RuntimeHelpers.GetUninitializedObject(typeof(ApplicationSettings));
		typeof(ApplicationSettings).GetField("_network", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(settings, services.GetNetwork());
		return settings;
	}

}
