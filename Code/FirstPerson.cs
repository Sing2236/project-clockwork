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
	public FirstPerson( Scene scene ) : base( scene )
	{
		// Every frame is more often than needed, but the controller can be replaced when a
		// character enters or leaves the city, and this has to survive that.
		Listen( Stage.StartUpdate, 0, Enforce, nameof( FirstPerson ) );
	}

	private void Enforce()
	{
		// Null before this client has a pawn, which is the case on the menu.
		if ( Hexagon.Player.Local is not { } pawn ) return;

		// The pawn may be disabled (out of sight, down, seated), so disabled components count.
		if ( pawn.GetComponent<PlayerController>( true ) is not { } controller ) return;

		controller.ThirdPerson = false;
	}
}
