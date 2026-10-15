using Sandbox;

namespace Hl2rp;

/// <summary>
/// Lets the local player fly: press <b>V</b> to leave the ground and go where you look, and V again
/// to come back down and walk again. For getting about a map you are building, not a game feature —
/// it is on the player's own machine only and does nothing for anyone else.
///
/// <para><b>Why it moves by teleport rather than by walking.</b> This is a Hexagon city, and Hexagon
/// checks movement. The host keeps its own belief about where a character is and rejects a claim it
/// could not have reached — a jump across the map at speed is exactly such a claim, so a noclip that
/// simply set a velocity would be snapped straight back, every frame, which is the classic
/// rubber-banded god-mode. The host's own teleport is not a claim: it moves the belief itself, so
/// flying is legal by construction. It is also what makes noclip a <i>noclip</i> — a teleport does
/// not care what is in the way.</para>
///
/// <para>It flies where you are looking, which is the only natural thing to do with a free camera:
/// forward is your forward, and the jump and duck keys rise and sink.</para>
/// </summary>
public sealed class Noclip : Component
{
	/// <summary>How fast it flies, in units a second. Fast enough to cross a 70,000-unit map in a minute.</summary>
	[Property] public float Speed { get; set; } = 900f;

	/// <summary>A boost while the run key is held, so a long map is quick to cross.</summary>
	[Property] public float Boost { get; set; } = 4f;

	private bool _flying;

	protected override void OnUpdate()
	{
		// Only the person playing this pawn flies it, and only their own machine decides. A proxy
		// belongs to somebody else.
		if ( IsProxy ) return;

		var player = GetComponent<Hexagon.Player>( true );
		var controller = GetComponent<PlayerController>( true );
		if ( player is not { HasCharacter: true } || controller is null ) return;

		if ( Input.Pressed( "Noclip" ) )
		{
			_flying = !_flying;
			Hexagon.Chat.Note( _flying ? "Noclip on — V to land." : "Noclip off." );
		}

		if ( !_flying ) return;

		var look = controller.EyeAngles.ToRotation();
		var wished = Input.AnalogMove;

		// Where you are looking, and up and down on the jump and duck keys.
		var way = (look.Forward * wished.y) + (look.Right * wished.x);
		way += Vector3.Up * (Input.Down( "Jump" ) ? 1f : 0f);
		way -= Vector3.Up * (Input.Down( "Duck" ) ? 1f : 0f);

		if ( way.Length < 0.001f ) return;

		var pace = Speed * (Input.Down( "Run" ) ? Boost : 1f);
		player.HostTeleport( player.HostPosition + way.Normal * pace * Time.Delta );
	}
}

/// <summary>
/// Puts <see cref="Noclip"/> on every pawn, so the local player has it whether or not a character
/// is in the city yet. Follows the same shape as <see cref="MoveSpeeds"/>: the system attaches once,
/// the component does the work.
/// </summary>
public sealed class NoclipToggle : GameObjectSystem<NoclipToggle>
{
	public NoclipToggle( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Attach, nameof( NoclipToggle ) );
	}

	/// <summary>A pawn is rebuilt whenever a character enters the city, so this keeps checking.</summary>
	private void Attach()
	{
		foreach ( var player in Scene.GetAllComponents<Hexagon.Player>() )
		{
			if ( !player.GameObject.IsValid() ) continue;
			if ( player.GameObject.GetComponent<Noclip>( true ) is not null ) continue;

			player.GameObject.AddComponent<Noclip>();
		}
	}
}
