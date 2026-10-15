using Sandbox;

namespace Hl2rp;

/// <summary>
/// Caps how fast anyone moves.
///
/// <para><b>Why this is written the way it is.</b> Hexagon sets <c>WalkSpeed</c> and <c>RunSpeed</c>
/// itself, at the top of <c>Player.OnUpdate</c>, from a tuple it captured once off the engine's
/// <c>PlayerController</c> (110 walking, 320 running). It recomputes those every frame, so a speed
/// set once — or set before Hexagon runs — is simply overwritten. This component must therefore run
/// <i>after</i> Hexagon's, which it does: s&amp;box updates components in the order they sit on the
/// object, and this is added after Hexagon's <c>Player</c> when the pawn is given one.</para>
///
/// <para><b>Why it takes the lower of the two rather than assigning.</b> Hexagon lowers these
/// values for its own reasons — someone limping on a wounded leg, or carrying another person, walks
/// more slowly, and that intent should survive. Taking <c>Min</c> caps everyone without flattening
/// those cases, and is idempotent: applying it twice changes nothing, so a frame where the order
/// came out the other way costs nothing permanent.</para>
/// </summary>
public sealed class MoveSpeeds : Component
{
	private MoveSpeedCaps Settings => MoveSpeedCaps.Current!;

	protected override void OnUpdate()
	{
		if ( Settings is not { } caps ) return;
		if ( GetComponent<PlayerController>( true ) is not { } controller ) return;

		controller.WalkSpeed = System.MathF.Min( caps.Walk, controller.WalkSpeed );
		controller.RunSpeed = System.MathF.Min( caps.Run, controller.RunSpeed );
		controller.DuckedSpeed = System.MathF.Min( caps.Ducked, controller.DuckedSpeed );
		controller.JumpSpeed = System.MathF.Min( caps.Jump, controller.JumpSpeed );
	}
}

/// <summary>
/// The caps <see cref="MoveSpeeds"/> applies, and which puts it on every pawn.
///
/// <para>These are properties so they can be tuned in Project Settings without a rebuild — the same
/// seam Hexagon uses for its own city rules. The defaults are roughly two-thirds of the engine's,
/// which is a deliberate walk rather than a jog.</para>
/// </summary>
public sealed class MoveSpeedCaps : GameObjectSystem<MoveSpeedCaps>
{
	[Property, Group( "Movement" )] public float Walk { get; set; } = 70f;
	[Property, Group( "Movement" )] public float Run { get; set; } = 190f;
	[Property, Group( "Movement" )] public float Ducked { get; set; } = 45f;
	[Property, Group( "Movement" )] public float Jump { get; set; } = 220f;

	public MoveSpeedCaps( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Attach, nameof( MoveSpeedCaps ) );
	}

	/// <summary>Gives every pawn the component, once. A pawn is rebuilt when a character enters the
	/// city, so this keeps checking rather than attaching once.</summary>
	private void Attach()
	{
		foreach ( var player in Scene.GetAllComponents<Hexagon.Player>() )
		{
			if ( !player.GameObject.IsValid() ) continue;
			if ( player.GameObject.GetComponent<MoveSpeeds>( true ) is not null ) continue;

			player.GameObject.AddComponent<MoveSpeeds>();
		}
	}
}
