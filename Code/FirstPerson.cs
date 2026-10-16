using Sandbox;

namespace Hl2rp;

/// <summary>
/// Keeps the local player's view in first person.
///
/// Hexagon gives every connection a pawn driven by the engine's <see cref="PlayerController"/>,
/// but it never sets that controller's perspective — and the engine's default for
/// <c>ThirdPerson</c> is <b>true</b>. So a server built on Hexagon is third-person out of the box.
/// Half-Life 2 roleplay is played from the eye, so this pins it to first person.
///
/// Only the pawn this client actually controls is touched. Another character's pawn is a proxy
/// here and its perspective belongs to whoever is playing it, so it is left alone.
///
/// This is setting code, not framework code: it belongs to this game rather than to Hexagon,
/// which is the split Hexagon's own documentation asks for.
/// </summary>
public sealed class FirstPerson : GameObjectSystem<FirstPerson>
{
	/// <summary>The action the engine watches to switch perspective. Bound to K in Input.config.</summary>
	private const string ToggleAction = "ThirdPerson";

	/// <summary>The pawn already set up, so the starting perspective is chosen once rather than
	/// re-imposed every frame.</summary>
	private Hexagon.Player _settled;

	public FirstPerson( Scene scene ) : base( scene )
	{
		// Every frame because the controller can be replaced when a character enters or leaves the
		// city, and the new one has to be set up too - but see below: it is only set up once each.
		Listen( Stage.StartUpdate, 0, Enforce, nameof( FirstPerson ) );
	}

	private void Enforce()
	{
		// Null before this client has a pawn, which is the case on the menu.
		if ( Hexagon.Player.Local is not { } pawn ) return;

		// The pawn may be disabled (out of sight, down, seated), so disabled components count.
		if ( pawn.GetComponent<PlayerController>( true ) is not { } controller ) return;

		// Only on a pawn we have not set up yet. Forcing this every frame was right while the view
		// was pinned, but it also meant nothing could ever switch away from it - the engine's own
		// toggle would flip ThirdPerson and this would flip it straight back. So the starting
		// perspective is chosen once, and the switching is handed to the controller: setting
		// ToggleCameraModeButton makes it watch that action and toggle itself, which is why the
		// button does not need reading here.
		if ( pawn == _settled ) return;
		_settled = pawn;

		controller.ThirdPerson = false;
		controller.ToggleCameraModeButton = ToggleAction;
	}
}
