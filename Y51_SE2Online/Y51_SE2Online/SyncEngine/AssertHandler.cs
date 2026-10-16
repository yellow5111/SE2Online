using Keen.VRage.Core.Diagnostics;
using Keen.VRage.Library.Diagnostics;

namespace Y51_SE2Online.SyncEngine;

/// <summary>
/// The Windows platform layer installs a handler that logs a failed game assertion and carries on; other platforms
/// (SE2 running natively on Linux through Pulsar) keep the default that throws, so every assertion aborted whatever was
/// running, e.g. building a replicated entity. This is the Windows handler without its dialog. Clients use it only while
/// a session runs, so single player keeps the platform's own behaviour; a dedicated server keeps it for the process.
/// </summary>
internal sealed class AssertHandler : DiagnosticHandlerBase
{
	private static IDiagnosticHandler? _previous;

	internal static void Install()
	{
		if (Assert.Handler is not FatalDiagnosticHandler) return;
		_previous = Assert.Handler;
		Assert.Handler = new AssertHandler();
		GameLog.Instance.Info("game assertions now log and continue (the platform left them fatal)");
	}

	internal static void Uninstall()
	{
		// Another plugin may have replaced the handler since; that one stays.
		if (_previous != null && Assert.Handler is AssertHandler) Assert.Handler = _previous;
		_previous = null;
	}

	protected override void ShowDialog(AssertionRecord record, string traceString, bool sourceFrame, ref bool abort) { }
}
