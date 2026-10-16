using Editor;
using Sandbox;

namespace Hl2rp.Editor;

/// <summary>
/// Installs the workshop packages the game's art comes from, every time the editor starts.
///
/// <para><b>Why this has to exist.</b> The armour and the weapon models are not the project's - they
/// belong to packages, and a package's assets are only present while that package is mounted. It was
/// believed that a reference in the project file would be enough; it is not. Four ways were tried and
/// all failed: <c>PackageReferences</c> in the .sbproj (a cold start leaves the asset unregistered),
/// <c>Package.MountAsync</c> from game code (dispatched, still unregistered), the <c>Mounts</c> field
/// in the .sbproj (accepted by the editor and ignored), and simply watching what the editor writes
/// when a package is installed - it writes nothing at all.</para>
///
/// <para>What does work is <c>AssetSystem.InstallAsync</c>, which is the call the editor's own
/// install uses. It is an <b>editor</b> API, so it cannot be reached from game code - which is why
/// this lives here rather than beside the rest of the game. Without it every editor restart leaves
/// Civil Protection in a plain body and the guns showing the engine's error model, until somebody
/// installs the packages by hand.</para>
///
/// <para><b>Not fixed by this:</b> the install is still per session. This makes it automatic, not
/// permanent - subscribing to the packages properly is still the thing that would make it stick.</para>
/// </summary>
public static class RequiredPackages
{
	/// <summary>
	/// Everything the game draws with, and where it comes from:
	/// the metrocop suit for Civil Protection, the two halves of each Facepunch weapon (the w_ world
	/// model and the v_ viewmodel), and a baton for the stun baton, which has no Half-Life port.
	/// </summary>
	private static readonly string[] Idents =
	{
		"playermodelports.metrocopclothes",
		"facepunch.w_usp",
		"facepunch.v_usp",
		"facepunch.w_mp5",
		"facepunch.v_mp5",
		"facepunch.w_m4a1",
		"facepunch.v_m4a1",
		"titanovsky.policebaton",
	};

	/// <summary>Set once the pass has begun, so the frame callback does not run it again.</summary>
	private static bool _started;

	/// <summary>When the editor's first frame was seen, so the install can wait a moment.</summary>
	private static RealTimeSince _sinceLoaded;

	/// <summary>Whether the settle clock has been started yet.</summary>
	private static bool _clockStarted;

	/// <summary>
	/// How long to let the editor settle before installing.
	///
	/// <para>Installing on the very first frame collides with the editor's own start-up: it is
	/// streaming textures as it loads, the packages land mid-stream, and the streaming list is
	/// invalidated underneath it. The engine says so, once per texture -
	/// <c>Skipping texture streaming: looks like the texture was reloaded while in the streaming
	/// list</c> - which is a page of warnings for something that is only badly timed. Waiting lets
	/// the start-up finish first.</para>
	/// </summary>
	private const float SettleSeconds = 8f;

	/// <summary>
	/// Runs once each time the editor starts, or a tool is rebuilt - after a short settle. Installing
	/// is idempotent and skips anything already present, so running it more often than needed costs
	/// nothing.
	/// </summary>
	[EditorEvent.Frame]
	public static async void Ensure()
	{
		if ( _started ) return;

		// The first frame only starts the clock, by resetting it - a RealTimeSince left at its
		// default reads as having elapsed an enormous time, which would fire the wait on frame one
		// and defeat the point.
		if ( !_clockStarted )
		{
			_clockStarted = true;
			_sinceLoaded = 0f;
			return;
		}

		if ( _sinceLoaded < SettleSeconds ) return;
		_started = true;

		foreach ( var ident in Idents )
		{
			try
			{
				// skipIfInstalled: anything already here returns immediately rather than downloading.
				var asset = await AssetSystem.InstallAsync( ident, true );
				Log.Info( $"[hl2rp] {ident} ready{( asset is null ? "" : $" ({asset.Path})" )}" );
			}
			catch ( System.Exception e )
			{
				// A package that will not install is worth knowing about but not worth stopping for:
				// the rest of the city still works without it.
				Log.Warning( $"[hl2rp] could not install {ident}: {e.Message}" );
			}
		}
	}
}
